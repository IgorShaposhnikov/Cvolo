using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using K4os.Compression.LZ4.Streams;

namespace Cvolo.Core.Packages;

/// <summary>
/// A memory-mapped handle to a validated .cvlib archive.
/// Retains ownership of the underlying mapped views and must be disposed.
/// </summary>
public sealed unsafe class CvlArchive : IDisposable
{
	private readonly MemoryMappedFile _mmf;
	private readonly MemoryMappedViewAccessor _accessor;
	private readonly byte* _basePointer;

	public CvlArchiveHeader Header { get; }
	public IReadOnlyList<CvlSectorIndexEntry> Sectors { get; }
	public CvlSliceManifest Manifest { get; }
	public bool IsUnsigned { get; }

	/// <summary>
	/// Zero-allocation view over the 32-byte Ed25519 public key stored at the start of the signature block.
	/// Unsigned archives expose 32 zero bytes.
	/// </summary>
	public ReadOnlySpan<byte> SigningPublicKey => GetRawSlice(Header.SignatureOffset, 32);

	/// <summary>
	/// Zero-allocation view over the 32-byte Merkle root hash stored in the container header (offset 0x20..0x3F).
	/// </summary>
	public ReadOnlySpan<byte> MerkleRootHash => new(_basePointer + 32, 32);

	/// <summary>
	/// Returns a zero-allocation span over an arbitrary validated byte range in the archive.
	/// The range is bounded by the header-declared file size and is useful for container
	/// metadata that does not belong to a logical sector (for example the signature block).
	/// </summary>
	public ReadOnlySpan<byte> GetRawSlice(ulong offset, int length)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(length);
		if (offset > Header.FileSize || (ulong)length > Header.FileSize - offset)
			throw new ArgumentOutOfRangeException(nameof(offset), $"Raw archive range [{offset}, {offset + (ulong)length}) exceeds file size {Header.FileSize}.");

		return new ReadOnlySpan<byte>(_basePointer + offset, length);
	}

	internal CvlArchive(
		MemoryMappedFile mmf,
		MemoryMappedViewAccessor accessor,
		byte* basePointer,
		CvlArchiveHeader header,
		IReadOnlyList<CvlSectorIndexEntry> sectors,
		CvlSliceManifest manifest,
		bool isUnsigned)
	{
		_mmf = mmf;
		_accessor = accessor;
		_basePointer = basePointer;
		Header = header;
		Sectors = sectors;
		Manifest = manifest;
		IsUnsigned = isUnsigned;
	}

	/// <summary>
	/// Returns a zero-allocation span over the requested sector's payload bytes.
	/// Sector IDs are 1-indexed (e.g., 1 = Metadata, 2 = Native Objects, etc.).
	/// Returns an empty span if the sector is missing or empty.
	/// </summary>
	public ReadOnlySpan<byte> GetSectorPayload(int sectorId)
	{
		if (sectorId < 1 || sectorId > Sectors.Count)
			throw new ArgumentOutOfRangeException(nameof(sectorId), $"Sector {sectorId} does not exist in this archive.");

		var entry = Sectors[sectorId - 1];
		if (entry.Length == 0)
			return ReadOnlySpan<byte>.Empty;

		return new ReadOnlySpan<byte>(_basePointer + entry.Offset, (int)entry.Length);
	}

	/// <summary>
	/// Decompresses Sector 5 (EMBEDDED_SOURCE_BUFFER) into the original UTF-8 `.cvl` source (§1.9).
	/// Returns an empty string when the sector is absent. Throws <see cref="CvlFormatException"/>
	/// (CVLF1913) on wrong LZ4 magic, truncated streams, advertised size over the 1 GiB limit,
	/// or invalid UTF-8 after decompression.
	/// </summary>
	public string ReadSourceBuffer()
	{
		if (Sectors.Count < 5 || Sectors[4].Length == 0)
			return string.Empty;

		var entry = Sectors[4];
		var payload = GetSectorPayload(5);

		// §1.9: length > 0 => payload MUST begin with the LZ4 frame magic 04 22 4D 18.
		if (payload.Length < 7 || payload[0] != 0x04 || payload[1] != 0x22 || payload[2] != 0x4D || payload[3] != 0x18)
			throw new CvlFormatException(CvlFormatDiagnosticIds.DecompressionFailed, (long)entry.Offset, "Sector 5 payload does not start with the LZ4 frame magic.");

		// Parse the LZ4 frame descriptor to read the advertised content size so we can reject
		// oversized frames before allocating (§1.9). Layout: Magic(4) FLG(1) BD(1)
		// [ContentSize(8) if FLG.0x08] [DictID(4) if FLG.0x01] HC(1).
		var flg = payload[4];
		long advertisedContentSize = 0;
		if ((flg & 0x08) != 0)
		{
			if (payload.Length < 14)
				throw new CvlFormatException(CvlFormatDiagnosticIds.DecompressionFailed, (long)entry.Offset, "Truncated LZ4 frame descriptor.");
			advertisedContentSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(6, 8));
		}

		const long MaxSourceSize = 1024L * 1024 * 1024; // CVL_MAX_SOURCE_SIZE = 1 GiB

		if (advertisedContentSize > MaxSourceSize)
			throw new CvlFormatException(CvlFormatDiagnosticIds.DecompressionFailed, (long)entry.Offset, $"Advertised LZ4 content size ({advertisedContentSize} bytes) exceeds the 1 GiB maximum.");

		byte[] decompressed;
		try
		{
			using var input = new MemoryStream(payload.ToArray());
			using var decoder = LZ4Stream.Decode(input);
			using var output = new MemoryStream();
			var buffer = new byte[81920];
			int read;
			long total = 0;
			while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
			{
				total += read;
				if (total > MaxSourceSize)
					throw new CvlFormatException(CvlFormatDiagnosticIds.DecompressionFailed, (long)entry.Offset, "Decompressed source exceeds the 1 GiB maximum.");
				output.Write(buffer, 0, read);
			}
			if (advertisedContentSize != 0 && total != advertisedContentSize)
				throw new CvlFormatException(CvlFormatDiagnosticIds.DecompressionFailed, (long)entry.Offset, $"Truncated LZ4 stream: advertised {advertisedContentSize} bytes but decoded {total}.");
			decompressed = output.ToArray();
		}
		catch (CvlFormatException) { throw; }
		catch (Exception)
		{
			throw new CvlFormatException(CvlFormatDiagnosticIds.DecompressionFailed, (long)entry.Offset, "LZ4 decompression failed.");
		}

		try
		{
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(decompressed);
		}
		catch (DecoderFallbackException)
		{
			throw new CvlFormatException(CvlFormatDiagnosticIds.DecompressionFailed, (long)entry.Offset, "Decompressed source is not valid UTF-8.");
		}
	}

	public void Dispose()
	{
		_accessor.SafeMemoryMappedViewHandle.ReleasePointer();
		_accessor.Dispose();
		_mmf.Dispose();
	}
}