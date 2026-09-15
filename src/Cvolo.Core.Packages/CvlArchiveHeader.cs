using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Cvolo.Core.Packages;

/// <summary>
/// The 80-byte root header of a .cvlib container (§1.2).
/// Padded to the first 4096-byte page boundary on disk.
/// All integer fields are structurally Little-Endian.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80, Pack = 1)]
public unsafe struct CvlArchiveHeader
{
	/// <summary>Must be exactly 'C', 'V', 'L', 'I', 'B', '\0'.</summary>
	[FieldOffset(0)]
	public fixed byte Magic[6];

	/// <summary>Version of the Cvolo compiler toolchain that packed this archive.</summary>
	[FieldOffset(6)]
	public ushort CompilerToolchainVersion;

	/// <summary>Intended LLVM backend version (e.g. 210 → LLVM v21.0).</summary>
	[FieldOffset(8)]
	public ushort IntendedLlvmBackendVersion;

	/// <summary>MUST be strictly zero.</summary>
	[FieldOffset(10)]
	public fixed byte Reserved[6];

	/// <summary>Page-aligned absolute byte offset to the Merkle index table.</summary>
	[FieldOffset(16)]
	public ulong MerkleIndexTableOffset;

	/// <summary>Page-aligned absolute byte offset to the 96-byte Ed25519 signature block.</summary>
	[FieldOffset(24)]
	public ulong SignatureOffset;

	/// <summary>BLAKE3-256 Merkle root hash, occupying exactly 0x20..0x3F.</summary>
	[FieldOffset(32)]
	public fixed byte MerkleRootHash[32];

	/// <summary>Number of physical sectors present (max 64).</summary>
	[FieldOffset(64)]
	public ulong SectorCount;

	/// <summary>Total container size, matching physical file length exactly.</summary>
	[FieldOffset(72)]
	public ulong FileSize;

	/// <summary>The expected 6-byte magic identifier.</summary>
	public static ReadOnlySpan<byte> ExpectedMagic => "CVLIB\0"u8;

	/// <summary>Reads the header from a span using strict little-endian encoding.</summary>
	public static CvlArchiveHeader ReadFrom(ReadOnlySpan<byte> span)
	{
		if (span.Length < 80)
			throw new ArgumentException("Span is too small to contain a CvlArchiveHeader.", nameof(span));

		var header = new CvlArchiveHeader();

		span.Slice(0, 6).CopyTo(MemoryMarshal.CreateSpan(ref header.Magic[0], 6));

		header.CompilerToolchainVersion = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
		header.IntendedLlvmBackendVersion = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2));

		span.Slice(10, 6).CopyTo(MemoryMarshal.CreateSpan(ref header.Reserved[0], 6));

		header.MerkleIndexTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16, 8));
		header.SignatureOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24, 8));

		span.Slice(32, 32).CopyTo(MemoryMarshal.CreateSpan(ref header.MerkleRootHash[0], 32));

		header.SectorCount = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(64, 8));
		header.FileSize = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(72, 8));

		return header;
	}

	/// <summary>Writes the header to a span using strict little-endian encoding.</summary>
	public readonly void WriteTo(Span<byte> span)
	{
		if (span.Length < 80)
			throw new ArgumentException("Span is too small to write a CvlArchiveHeader.", nameof(span));

		fixed (byte* m = Magic)
		{
			new ReadOnlySpan<byte>(m, 6).CopyTo(span.Slice(0, 6));
		}

		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), CompilerToolchainVersion);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), IntendedLlvmBackendVersion);

		fixed (byte* r = Reserved)
		{
			new ReadOnlySpan<byte>(r, 6).CopyTo(span.Slice(10, 6));
		}

		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16, 8), MerkleIndexTableOffset);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24, 8), SignatureOffset);

		fixed (byte* h = MerkleRootHash)
		{
			new ReadOnlySpan<byte>(h, 32).CopyTo(span.Slice(32, 32));
		}

		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(64, 8), SectorCount);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(72, 8), FileSize);
	}
}