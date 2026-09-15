using System.IO.MemoryMappedFiles;

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

	/// <summary>
	/// Zero-allocation view over the 32-byte Merkle root hash stored in the container header (offset 0x20..0x3F).
	/// </summary>
	public ReadOnlySpan<byte> MerkleRootHash => new(_basePointer + 32, 32);

	internal CvlArchive(
		MemoryMappedFile mmf,
		MemoryMappedViewAccessor accessor,
		byte* basePointer,
		CvlArchiveHeader header,
		IReadOnlyList<CvlSectorIndexEntry> sectors,
		CvlSliceManifest manifest)
	{
		_mmf = mmf;
		_accessor = accessor;
		_basePointer = basePointer;
		Header = header;
		Sectors = sectors;
		Manifest = manifest;
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

	public void Dispose()
	{
		_accessor.SafeMemoryMappedViewHandle.ReleasePointer();
		_accessor.Dispose();
		_mmf.Dispose();
	}
}