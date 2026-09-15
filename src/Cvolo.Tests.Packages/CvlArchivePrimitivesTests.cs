using System.Runtime.InteropServices;
using Cvolo.Core.Packages;

namespace Cvolo.Tests.Packages;

public sealed class CvlArchivePrimitivesTests
{
	[Fact]
	public void Header_Matches_StrictSize_Constraints()
	{
		Assert.Equal(80, Marshal.SizeOf<CvlArchiveHeader>());
	}

	[Fact]
	public void IndexEntry_Matches_StrictSize_Constraints()
	{
		Assert.Equal(32, Marshal.SizeOf<CvlSectorIndexEntry>());
	}

	[Fact]
	public void Header_ReadFrom_RejectsTooSmallSpan()
	{
		var span = new byte[79];
		Assert.Throws<ArgumentException>(() => CvlArchiveHeader.ReadFrom(span));
	}

	[Fact]
	public void Header_WriteTo_RejectsTooSmallSpan()
	{
		var header = new CvlArchiveHeader();
		var span = new byte[79];
		Assert.Throws<ArgumentException>(() => header.WriteTo(span));
	}

	[Fact]
	public void IndexEntry_RejectsTooSmallSpan()
	{
		var span = new byte[31];
		Assert.Throws<ArgumentException>(() => CvlSectorIndexEntry.ReadFrom(span));

		var entry = new CvlSectorIndexEntry(offset: 4096, length: 100, startLeafIndex: 1, leafCount: 1, reserved: 0);
		Assert.Throws<ArgumentException>(() => entry.WriteTo(span));
	}

	[Fact]
	public unsafe void Header_RoundTrips_AllFields()
	{
		var buffer = new byte[80];

		var original = new CvlArchiveHeader
		{
			CompilerToolchainVersion = 1,
			IntendedLlvmBackendVersion = 210,
			MerkleIndexTableOffset = 4096,
			SignatureOffset = 8192,
			SectorCount = 5,
			FileSize = 10000
		};

		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(original.Magic, 6));

		for (var i = 0; i < 32; i++)
			original.MerkleRootHash[i] = (byte)(i + 1);

		original.WriteTo(buffer);

		var reconstructed = CvlArchiveHeader.ReadFrom(buffer);

		Assert.Equal(original.CompilerToolchainVersion, reconstructed.CompilerToolchainVersion);
		Assert.Equal(original.IntendedLlvmBackendVersion, reconstructed.IntendedLlvmBackendVersion);
		Assert.Equal(original.MerkleIndexTableOffset, reconstructed.MerkleIndexTableOffset);
		Assert.Equal(original.SignatureOffset, reconstructed.SignatureOffset);
		Assert.Equal(original.SectorCount, reconstructed.SectorCount);
		Assert.Equal(original.FileSize, reconstructed.FileSize);

		var originalMagic = new ReadOnlySpan<byte>(original.Magic, 6);
		var reconstructedMagic = new ReadOnlySpan<byte>(reconstructed.Magic, 6);
		Assert.True(originalMagic.SequenceEqual(reconstructedMagic));

		var originalHash = new ReadOnlySpan<byte>(original.MerkleRootHash, 32);
		var reconstructedHash = new ReadOnlySpan<byte>(reconstructed.MerkleRootHash, 32);
		Assert.True(originalHash.SequenceEqual(reconstructedHash));
	}

	[Fact]
	public void IndexEntry_RoundTrips_AllFields()
	{
		var buffer = new byte[32];

		var original = new CvlSectorIndexEntry(
			offset: 16384,
			length: 2048,
			startLeafIndex: 1,
			leafCount: 1,
			reserved: 0);

		original.WriteTo(buffer);

		var reconstructed = CvlSectorIndexEntry.ReadFrom(buffer);

		Assert.Equal(original.Offset, reconstructed.Offset);
		Assert.Equal(original.Length, reconstructed.Length);
		Assert.Equal(original.StartLeafIndex, reconstructed.StartLeafIndex);
		Assert.Equal(original.LeafCount, reconstructed.LeafCount);
		Assert.Equal(original.Reserved, reconstructed.Reserved);
	}

	[Fact]
	public unsafe void Header_IsStructurallyLittleEndian_AtExpectedOffsets()
	{
		var buffer = new byte[80];
		var header = new CvlArchiveHeader
		{
			CompilerToolchainVersion = 0x0201,
			IntendedLlvmBackendVersion = 0xD203,
			MerkleIndexTableOffset = 0x1122334455667788,
			SignatureOffset = 0x8877665544332211,
			SectorCount = 5,
			FileSize = 0xAABBCCDDEEFF0011
		};
		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));
		header.WriteTo(buffer);

		// Magic at 0x00
		Assert.Equal((byte)'C', buffer[0]);
		Assert.Equal((byte)'V', buffer[1]);
		Assert.Equal((byte)'L', buffer[2]);
		Assert.Equal((byte)'I', buffer[3]);
		Assert.Equal((byte)'B', buffer[4]);
		Assert.Equal(0, buffer[5]);

		// CompilerToolchainVersion LE at 6
		Assert.Equal(0x01, buffer[6]);
		Assert.Equal(0x02, buffer[7]);

		// IntendedLlvmBackendVersion LE at 8
		Assert.Equal(0x03, buffer[8]);
		Assert.Equal(0xD2, buffer[9]);

		// Reserved at 10..15 zero
		for (var i = 10; i < 16; i++)
			Assert.Equal(0, buffer[i]);

		// MerkleIndexTableOffset LE at 16
		Assert.Equal(0x88, buffer[16]);
		Assert.Equal(0x77, buffer[17]);
		Assert.Equal(0x66, buffer[18]);
		Assert.Equal(0x55, buffer[19]);
		Assert.Equal(0x44, buffer[20]);
		Assert.Equal(0x33, buffer[21]);
		Assert.Equal(0x22, buffer[22]);
		Assert.Equal(0x11, buffer[23]);

		// SignatureOffset LE at 24
		Assert.Equal(0x11, buffer[24]);
		Assert.Equal(0x22, buffer[25]);

		// SectorCount LE at 64
		Assert.Equal(5, buffer[64]);

		// FileSize LE at 72
		Assert.Equal(0x11, buffer[72]);
		Assert.Equal(0x00, buffer[73]);
	}

	[Fact]
	public void FormatException_Carries_Code_Offset_Detail()
	{
		var exception = new CvlFormatException(
			CvlFormatDiagnosticIds.HeaderMalformed,
			offset: 0,
			"Magic number mismatch.",
			"Found XYZ instead of CVLIB");

		Assert.Equal("CVLF1900", exception.Code);
		Assert.Equal(0, exception.Offset);
		Assert.Equal("Magic number mismatch.", exception.Message);
		Assert.Contains("Found XYZ", exception.Detail);
		Assert.Contains("[CVLF1900 at offset 0x00000000]", exception.ToString());
	}
}