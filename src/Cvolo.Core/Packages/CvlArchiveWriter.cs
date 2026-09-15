using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Blake3;
using K4os.Compression.LZ4.Streams;
using NSec.Cryptography;

namespace Cvolo.Core.Packages;

/// <summary>
/// Packages artifacts into a deterministic, page-aligned, Merkle-anchored .cvlib container.
/// Implements the 3-pass writer pipeline defined in §1.6 of the CVLF specification:
/// (1) layout, (2) streaming BLAKE3 hash, (3) physical write + atomic rename.
/// </summary>
public sealed class CvlArchiveWriter
{
	private const uint PageSize = 4096;
	private const ulong MaxContainerSize = 2UL * 1024 * 1024 * 1024; // 2 GiB
	private const int MinimumSectorCount = 5;

	private readonly Dictionary<int, ReadOnlyMemory<byte>> _sectors = [];

	/// <summary>Compiler toolchain version recorded in the container header (default: 1).</summary>
	public ushort CompilerToolchainVersion { get; set; } = 1;

	/// <summary>Intended LLVM backend target version recorded in the header (default: 210 -> LLVM v21.0).</summary>
	public ushort IntendedLlvmBackendVersion { get; set; } = 210;

	/// <summary>
	/// Sets the binary payload for a physical sector (1-indexed, 1..64).
	/// Sectors without an explicit payload are emitted as empty (Length 0).
	/// </summary>
	public void SetSector(int sectorId, ReadOnlyMemory<byte> payload)
	{
		if (sectorId < 1 || sectorId > 64)
			throw new ArgumentOutOfRangeException(nameof(sectorId), "Sector ID must be between 1 and 64.");

		_sectors[sectorId] = payload;
	}

	/// <summary>
	/// Convenience helper: sets Sector 5 (EMBEDDED_SOURCE_BUFFER) to an LZ4-frame
	/// compressed UTF-8 source payload as defined in §1.9.
	/// </summary>
	public void SetSourceBuffer(string sourceCvl)
	{
		using var ms = new MemoryStream();
		using (var lz4 = LZ4Stream.Encode(ms))
		{
			var sourceBytes = Encoding.UTF8.GetBytes(sourceCvl);
			lz4.Write(sourceBytes, 0, sourceBytes.Length);
		}

		SetSector(5, ms.ToArray());
	}

	/// <summary>
	/// Executes the 3-pass deterministic write pipeline and atomically moves the
	/// finished container onto <paramref name="outputPath"/> via &lt;name&gt;.tmp_&lt;pid&gt;.
	/// </summary>
	/// <param name="outputPath">Destination path of the .cvlib archive.</param>
	/// <param name="signingKey">Ed25519 signing key. Signs strictly the 32-byte Merkle root hash (§1.3/§5).</param>
	public unsafe void Write(string outputPath, Key signingKey)
	{
		if (signingKey.Algorithm != SignatureAlgorithm.Ed25519)
			throw new ArgumentException("Signing key must be an Ed25519 key.", nameof(signingKey));

		// Sector 1 (COMPLIANCE_METADATA) is mandatory and must carry the slice manifest.
		if (!_sectors.TryGetValue(1, out var sector1) || sector1.Length == 0)
			throw new InvalidOperationException("Sector 1 (COMPLIANCE_METADATA) is mandatory and must not be empty.");

		// A canonical container always emits Sectors 1..5 (§1.5/§1.8); higher sectors are appended if set.
		var maxSectorId = _sectors.Keys.Max();
		var sectorCount = (ulong)Math.Max(MinimumSectorCount, maxSectorId);

		// =========================================================================
		// Pass 1: Layout
		// =========================================================================
		var merkleIndexTableOffset = 4096UL;
		var indexTableSize = sectorCount * 32UL;
		var signatureOffset = merkleIndexTableOffset + AlignUp(indexTableSize, PageSize);

		var indexEntries = new CvlSectorIndexEntry[sectorCount];
		var currentOffset = signatureOffset + AlignUp(96UL, PageSize); // signature block: 32B pubkey + 64B sig
		uint currentLeafIndex = 1; // leaf 0 is reserved for the archive header

		for (var i = 0; i < (int)sectorCount; i++)
		{
			var sectorId = i + 1;
			var hasPayload = _sectors.TryGetValue(sectorId, out var payload) && payload.Length > 0;

			if (!hasPayload)
			{
				// Empty sectors share the current page boundary (offset does not advance)
				// and contribute exactly one domain-separated `empty_leaf` marker.
				indexEntries[i] = new CvlSectorIndexEntry(
					offset: currentOffset,
					length: 0,
					startLeafIndex: currentLeafIndex,
					leafCount: 1);

				currentLeafIndex += 1;
			}
			else
			{
				var length = (ulong)payload.Length;
				var pages = (uint)((length + (PageSize - 1)) / PageSize);
				var alignedLength = AlignUp(length, PageSize);

				indexEntries[i] = new CvlSectorIndexEntry(
					offset: currentOffset,
					length: length,
					startLeafIndex: currentLeafIndex,
					leafCount: pages);

				currentLeafIndex += pages;
				currentOffset += alignedLength;
			}
		}

		var fileSize = currentOffset;
		if (fileSize > MaxContainerSize)
			throw new InvalidOperationException($"Computed container size ({fileSize} bytes) exceeds the 2 GiB limit.");

		var header = new CvlArchiveHeader
		{
			CompilerToolchainVersion = CompilerToolchainVersion,
			IntendedLlvmBackendVersion = IntendedLlvmBackendVersion,
			MerkleIndexTableOffset = merkleIndexTableOffset,
			SignatureOffset = signatureOffset,
			SectorCount = sectorCount,
			FileSize = fileSize
		};
		CvlArchiveHeader.ExpectedMagic.CopyTo(MemoryMarshal.CreateSpan(ref header.Magic[0], 6));

		// =========================================================================
		// Pass 2: Streaming BLAKE3 hash
		// =========================================================================
		var leaves = new List<byte[]>();

		// leaf_0 = BLAKE3(header[0..80]) WITHOUT padding; MerkleRootHash (bytes 32..63)
		// is normalized to zero during the hash pass since the root does not exist yet.
		var headerBytes = new byte[80];
		header.WriteTo(headerBytes);
		Array.Clear(headerBytes, 32, 32);
		leaves.Add(HashBytes(headerBytes));

		for (var i = 0; i < (int)sectorCount; i++)
		{
			var sectorId = (uint)(i + 1);
			var entry = indexEntries[i];

			if (entry.Length == 0)
			{
				// empty_leaf(sector_id) = BLAKE3( 0x00 0x00 0x00 0x00 || uint32_le(sector_id) )
				var emptyMarker = new byte[8];
				BinaryPrimitives.WriteUInt32LittleEndian(emptyMarker.AsSpan(4, 4), sectorId);
				leaves.Add(HashBytes(emptyMarker));
			}
			else
			{
				leaves.AddRange(HashSectorPages(_sectors[(int)sectorId]));
			}
		}

		var rootHash = MerkleRoot(leaves);
		rootHash.CopyTo(new Span<byte>(header.MerkleRootHash, 32));

		// Ed25519 signature strictly over the 32-byte Merkle root (§1.3, §5).
		var algorithm = SignatureAlgorithm.Ed25519;
		var signature = algorithm.Sign(signingKey, rootHash);
		var publicKey = signingKey.Export(KeyBlobFormat.RawPublicKey);

		// =========================================================================
		// Pass 3: Write + atomic rename
		// =========================================================================
		var tempPath = $"{outputPath}.tmp_{Environment.ProcessId}";

		try
		{
			using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				// Header page: 80-byte header + zero padding to 4096.
				var headerPage = new byte[PageSize];
				header.WriteTo(headerPage);
				fs.Write(headerPage);

				// Index table + zero padding up to the signature block.
				var indexBytes = new byte[signatureOffset - merkleIndexTableOffset];
				for (var i = 0; i < (int)sectorCount; i++)
					indexEntries[i].WriteTo(indexBytes.AsSpan(i * 32, 32));
				fs.Write(indexBytes);

				// Signature block page: 32B public key + 64B signature + zero padding.
				var sigBlockPage = new byte[PageSize];
				publicKey.CopyTo(sigBlockPage, 0);
				signature.CopyTo(sigBlockPage, 32);
				fs.Write(sigBlockPage);

				// Sector payloads in declared order, each zero-padded to its page boundary.
				for (var i = 0; i < (int)sectorCount; i++)
				{
					var sectorId = i + 1;
					var entry = indexEntries[i];
					if (entry.Length == 0)
						continue;

					var payload = _sectors[sectorId].Span;
					fs.Write(payload);

					var paddingCount = (int)(AlignUp(entry.Length, PageSize) - entry.Length);
					if (paddingCount > 0)
						fs.Write(new byte[paddingCount]);
				}

				fs.Flush(true); // Hard fsync before the atomic rename.
			}

			File.Move(tempPath, outputPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(tempPath))
			{
				try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
			}
		}
	}

	/// <summary>
	/// Hashes a sector's payload as full 4096-byte pages. The final page is
	/// zero-padded to exactly one page — including when the sector is empty.
	/// </summary>
	private static IEnumerable<byte[]> HashSectorPages(ReadOnlyMemory<byte> payload)
	{
		var fullPages = payload.Length / (int)PageSize;
		var remainder = payload.Length % (int)PageSize;

		for (var p = 0; p < fullPages; p++)
			yield return HashBytes(payload.Slice(p * (int)PageSize, (int)PageSize).Span);

		if (remainder > 0)
		{
			var padded = new byte[PageSize];
			payload.Slice(fullPages * (int)PageSize, remainder).Span.CopyTo(padded);
			yield return HashBytes(padded);
		}
	}

	/// <summary>
	/// Reduces the flat leaf vector into a balanced binary Merkle root:
	/// parent = BLAKE3(left || right); an odd node at a level is promoted unchanged.
	/// </summary>
	private static byte[] MerkleRoot(List<byte[]> leaves)
	{
		while (leaves.Count > 1)
		{
			var nextLevel = new List<byte[]>((leaves.Count + 1) / 2);
			for (var i = 0; i < leaves.Count; i += 2)
			{
				if (i + 1 < leaves.Count)
				{
					var concat = new byte[64];
					leaves[i].CopyTo(concat, 0);
					leaves[i + 1].CopyTo(concat, 32);
					nextLevel.Add(HashBytes(concat));
				}
				else
				{
					nextLevel.Add(leaves[i]);
				}
			}
			leaves = nextLevel;
		}

		return leaves[0];
	}

	private static byte[] HashBytes(byte[] bytes) =>
		Hasher.Hash(bytes).AsSpan().ToArray();

	private static byte[] HashBytes(ReadOnlySpan<byte> bytes) =>
		Hasher.Hash(bytes).AsSpan().ToArray();

	private static ulong AlignUp(ulong value, uint alignment) =>
		(value + (alignment - 1)) & ~(ulong)(alignment - 1);
}