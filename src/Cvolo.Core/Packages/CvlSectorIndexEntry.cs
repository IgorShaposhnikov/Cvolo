using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Cvolo.Core.Packages;

/// <summary>
/// A 32-byte index table entry describing a single sector's logical and physical bounds (§1.8).
/// All integer fields are structurally Little-Endian.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32, Pack = 1)]
public readonly struct CvlSectorIndexEntry
{
	/// <summary>Page-aligned absolute byte offset where the sector begins.</summary>
	[FieldOffset(0)]
	public readonly ulong Offset;

	/// <summary>Logical payload length. 0 marks an empty sector.</summary>
	[FieldOffset(8)]
	public readonly ulong Length;

	/// <summary>Absolute Merkle leaf index where this sector's page hashes begin (0 = header leaf).</summary>
	[FieldOffset(16)]
	public readonly uint StartLeafIndex;

	/// <summary>Number of Merkle leaves spanning this sector; 1 for empty sectors.</summary>
	[FieldOffset(20)]
	public readonly uint LeafCount;

	/// <summary>MUST be strictly zero.</summary>
	[FieldOffset(24)]
	public readonly ulong Reserved;

	public CvlSectorIndexEntry(ulong offset, ulong length, uint startLeafIndex, uint leafCount, ulong reserved = 0)
	{
		Offset = offset;
		Length = length;
		StartLeafIndex = startLeafIndex;
		LeafCount = leafCount;
		Reserved = reserved;
	}

	/// <summary>Reads an entry from a span using strict little-endian encoding.</summary>
	public static CvlSectorIndexEntry ReadFrom(ReadOnlySpan<byte> span)
	{
		if (span.Length < 32)
			throw new ArgumentException("Span is too small to contain a CvlSectorIndexEntry.", nameof(span));

		return new CvlSectorIndexEntry(
			offset: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(0, 8)),
			length: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8)),
			startLeafIndex: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4)),
			leafCount: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4)),
			reserved: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24, 8)));
	}

	/// <summary>Writes the entry to a span using strict little-endian encoding.</summary>
	public void WriteTo(Span<byte> span)
	{
		if (span.Length < 32)
			throw new ArgumentException("Span is too small to write a CvlSectorIndexEntry.", nameof(span));

		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(0, 8), Offset);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8, 8), Length);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), StartLeafIndex);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20, 4), LeafCount);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24, 8), Reserved);
	}
}