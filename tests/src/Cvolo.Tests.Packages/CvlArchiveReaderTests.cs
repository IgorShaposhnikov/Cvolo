using System.Buffers.Binary;
using System.Text;
using Blake3;
using Cvolo.Core.Packages;
using NSec.Cryptography;

namespace Cvolo.Tests.Packages;

public sealed class CvlArchiveReaderTests : IDisposable
{
	private readonly string _tempFile;

	public CvlArchiveReaderTests()
	{
		_tempFile = Path.GetTempFileName();
	}

	public void Dispose()
	{
		if (File.Exists(_tempFile))
			File.Delete(_tempFile);
	}

	private static byte[] Hash(ReadOnlySpan<byte> data) => Hasher.Hash(data).AsSpan().ToArray();

	private static byte[] HashEmptyLeaf(uint sectorId)
	{
		var marker = new byte[8];
		BinaryPrimitives.WriteUInt32LittleEndian(marker.AsSpan(4, 4), sectorId);
		return Hash(marker);
	}

	/// <summary>
	/// Writes a 16 KiB fake archive that is fully valid: valid magic, page-aligned index/signature/sector,
	/// a correctly computed Merkle root, and a valid Ed25519 signature over that root.
	/// Optional mutations simulate on-disk tampering AFTER the root/signature were computed.
	/// </summary>
	private unsafe void WriteFakeValidArchive(
		string path,
		bool mutateSignature = false,
		bool mutatePayload = false,
		bool mutateRootHash = false,
		uint? overrideManifestLength = null,
		bool emptySector1 = false)
	{
		using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

		var header = new CvlArchiveHeader
		{
			CompilerToolchainVersion = 1,
			IntendedLlvmBackendVersion = 210,
			MerkleIndexTableOffset = 4096,
			SignatureOffset = 8192,
			SectorCount = 1,
			FileSize = 16384 // Header(4k) + Index(4k) + Sig(4k) + Sector1(4k)
		};
		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));

		// Sector 1 payload: [uint32_le manifest length][JSON manifest][layout metadata / zero padding].
		var manifestJson = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
		var jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
		var payload = new byte[4096];
		var declaredManifestLength = overrideManifestLength ?? (uint)jsonBytes.Length;
		BinaryPrimitives.WriteUInt32LittleEndian(payload, declaredManifestLength);
		jsonBytes.CopyTo(payload, 4);

		var sector1Length = emptySector1 ? 0UL : 100UL;
		var sector1 = new CvlSectorIndexEntry(offset: 12288, length: sector1Length, startLeafIndex: 1, leafCount: 1, reserved: 0);

		// Compute leaves. leaf_0 = BLAKE3(header[0..80]) while MerkleRootHash is still zero.
		var leaves = new List<byte[]>();
		var headerBytes = new byte[80];
		header.WriteTo(headerBytes);
		leaves.Add(Hash(headerBytes));
		leaves.Add(emptySector1 ? HashEmptyLeaf(1) : Hash(payload));

		var concat = new byte[64];
		leaves[0].CopyTo(concat, 0);
		leaves[1].CopyTo(concat, 32);
		var root = Hash(concat);
		if (mutateRootHash) root[0] ^= 0xFF;
		root.CopyTo(new Span<byte>(header.MerkleRootHash, 32));

		// Write header page.
		var headerPage = new byte[4096];
		header.WriteTo(headerPage);
		fs.Write(headerPage);

		// Write index page.
		var indexPage = new byte[4096];
		sector1.WriteTo(indexPage);
		fs.Write(indexPage);

		// Sign the stored root and write the signature page.
		var algorithm = SignatureAlgorithm.Ed25519;
		using var key = Key.Create(algorithm);
		var signature = algorithm.Sign(key, root);
		var publicKey = key.Export(KeyBlobFormat.RawPublicKey);
		if (mutateSignature) signature[0] ^= 0xFF;

		var sigPage = new byte[4096];
		publicKey.CopyTo(sigPage, 0);
		signature.CopyTo(sigPage, 32);
		fs.Write(sigPage);

		// Write the sector 1 payload page (optional on-disk corruption).
		// Tamper a byte inside the logical 100-byte payload but beyond the JSON manifest
		// (the trailing layout-metadata region), so the manifest parse is unaffected while
		// the hashed page bytes change.
		if (mutatePayload) payload[70] ^= 0xFF;
		fs.Write(payload);
	}

	private unsafe delegate void HeaderMutator(CvlArchiveHeader* header);

	private unsafe void WriteRawHeaderFile(string path, HeaderMutator mutate)
	{
		using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
		var header = new CvlArchiveHeader
		{
			CompilerToolchainVersion = 1,
			IntendedLlvmBackendVersion = 210,
			MerkleIndexTableOffset = 4096,
			SignatureOffset = 8192,
			SectorCount = 1,
			FileSize = 16384
		};
		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));

		mutate(&header);

		var size = header.FileSize >= 4096 ? (int)header.FileSize : 4096;
		var buffer = new byte[size];
		header.WriteTo(buffer);
		fs.Write(buffer);
	}

	/// <summary>
	/// Writes an archive with the given layout but without any cryptographic material.
	/// Only usable for tests that fail during structural validation (before verification).
	/// </summary>
	private void WriteRawLayout(CvlArchiveHeader header, CvlSectorIndexEntry[] entries)
	{
		using var fs = new FileStream(_tempFile, FileMode.Create, FileAccess.Write);

		var headerPage = new byte[4096];
		header.WriteTo(headerPage);
		fs.Write(headerPage);

		var indexPage = new byte[4096];
		for (var i = 0; i < entries.Length; i++)
			entries[i].WriteTo(indexPage.AsSpan(i * 32, 32));
		fs.Write(indexPage);

		fs.SetLength((long)header.FileSize);
	}

	[Fact]
	public void Read_ValidArchive_PassesAllChecks()
	{
		WriteFakeValidArchive(_tempFile);

		using var archive = CvlArchiveReader.Read(_tempFile);

		Assert.NotNull(archive);
		Assert.Equal(1UL, archive.Header.SectorCount);
		Assert.Empty(archive.Manifest.Slices);

		// Payload fidelity must honour the logical Length (100), not the padded page size.
		var s1Payload = archive.GetSectorPayload(1);
		Assert.Equal(100, s1Payload.Length);
	}

	[Fact]
	public void Read_ValidArchive_HeaderFieldsAreExposed()
	{
		WriteFakeValidArchive(_tempFile);

		using var archive = CvlArchiveReader.Read(_tempFile);

		Assert.Equal(1, archive.Header.CompilerToolchainVersion);
		Assert.Equal(210, archive.Header.IntendedLlvmBackendVersion);
		Assert.Equal(32, archive.MerkleRootHash.Length);
		Assert.Single(archive.Sectors);
		Assert.Equal(12288UL, archive.Sectors[0].Offset);
	}

	[Fact]
	public void Read_TamperedSignature_ThrowsCVLF1902()
	{
		WriteFakeValidArchive(_tempFile, mutateSignature: true);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.BitcodeTampered, ex.Code);
		Assert.Contains("signature verification failed", ex.Message);
	}

	[Fact]
	public void Read_TamperedRootHash_ThrowsCVLF1902()
	{
		WriteFakeValidArchive(_tempFile, mutateRootHash: true);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.BitcodeTampered, ex.Code);
		Assert.Contains("Merkle root verification failed", ex.Message);
	}

	[Fact]
	public void Read_TamperedPayload_ThrowsCVLF1902()
	{
		WriteFakeValidArchive(_tempFile, mutatePayload: true);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.BitcodeTampered, ex.Code);
		Assert.Contains("Merkle root verification failed", ex.Message);
	}

	[Fact]
	public void Read_TamperedPayload_WithSkippedMerkle_Passes()
	{
		WriteFakeValidArchive(_tempFile, mutatePayload: true);

		using var archive = CvlArchiveReader.Read(_tempFile, skipMerkleHashVerification: true);
		Assert.NotNull(archive);
	}

	[Fact]
	public void Read_FileTooSmall_ThrowsCVLF1900()
	{
		File.WriteAllBytes(_tempFile, new byte[2000]);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.HeaderMalformed, ex.Code);
	}

	[Fact]
	public unsafe void Read_FileSizeMismatch_ThrowsCVLF1910()
	{
		using (var fs = new FileStream(_tempFile, FileMode.Create, FileAccess.Write))
		{
			var header = new CvlArchiveHeader
			{
				CompilerToolchainVersion = 1,
				IntendedLlvmBackendVersion = 210,
				MerkleIndexTableOffset = 4096,
				SignatureOffset = 8192,
				SectorCount = 1,
				FileSize = 20480 // Claims 20480; the physical file is only 16384 bytes.
			};
			CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));

			var buffer = new byte[16384];
			header.WriteTo(buffer);
			fs.Write(buffer);
		}

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.FileSizeMismatch, ex.Code);
		Assert.Contains("mismatches physical file size", ex.Message);
	}

	[Fact]
	public unsafe void Read_MagicMismatch_ThrowsCVLF1900()
	{
		WriteRawHeaderFile(_tempFile, h => "CVLIX\0"u8.CopyTo(new Span<byte>(h->Magic, 6)));

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.HeaderMalformed, ex.Code);
		Assert.Contains("Invalid magic bytes", ex.Message);
	}

	[Fact]
	public unsafe void Read_ReservedNonZero_ThrowsCVLF1900()
	{
		WriteRawHeaderFile(_tempFile, h => h->Reserved[0] = 1);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.HeaderMalformed, ex.Code);
		Assert.Contains("Reserved header bytes must be zero", ex.Message);
	}

	[Fact]
	public unsafe void Read_MisalignedIndexTable_ThrowsCVLF1911()
	{
		WriteRawHeaderFile(_tempFile, h => h->MerkleIndexTableOffset = 4000);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorBoundsInvalid, ex.Code);
		Assert.Contains("must be page-aligned", ex.Message);
	}

	[Fact]
	public unsafe void Read_SignatureOffsetPastFile_ThrowsCVLF1911()
	{
		WriteRawHeaderFile(_tempFile, h => h->SignatureOffset = 16384);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorBoundsInvalid, ex.Code);
		Assert.Contains("extends beyond the end of the file", ex.Message);
	}

	[Fact]
	public unsafe void Read_SectorCountExceedsLimit_ThrowsCVLF1911()
	{
		WriteRawHeaderFile(_tempFile, h => h->SectorCount = 65);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorBoundsInvalid, ex.Code);
		Assert.Contains("exceeds the maximum allowed", ex.Message);
	}

	[Fact]
	public unsafe void Read_ZeroSectors_ThrowsCVLF1911InsteadOfIndexError()
	{
		WriteRawHeaderFile(_tempFile, h => h->SectorCount = 0);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorBoundsInvalid, ex.Code);
		Assert.Contains("Sector 1", ex.Message);
	}

	[Fact]
	public unsafe void Read_IndexTableOverflowsReservedSpace_ThrowsCVLF1911()
	{
		// SignatureOffset == MerkleIndexTableOffset leaves zero room for the index table.
		WriteRawHeaderFile(_tempFile, h => h->SignatureOffset = 4096);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorBoundsInvalid, ex.Code);
		Assert.Contains("overflows the reserved space", ex.Message);
	}

	[Fact]
	public unsafe void Read_SectorReservedNonZero_ThrowsCVLF1911()
	{
		var header = CreateTwoSectorHeader();
		header.FileSize = 24576;
		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));

		var entries = new[]
		{
			new CvlSectorIndexEntry(offset: 12288, length: 0, startLeafIndex: 1, leafCount: 1, reserved: 0),
			new CvlSectorIndexEntry(offset: 12288, length: 0, startLeafIndex: 2, leafCount: 1, reserved: 1)
		};

		WriteRawLayout(header, entries);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorBoundsInvalid, ex.Code);
		Assert.Contains("reserved bytes must be zero", ex.Message);
	}

	[Fact]
	public unsafe void Read_SectorOutOfOrder_ThrowsCVLF1912()
	{
		var header = CreateTwoSectorHeader();
		header.FileSize = 24576;
		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));

		var entries = new[]
		{
			new CvlSectorIndexEntry(offset: 20480, length: 4096, startLeafIndex: 1, leafCount: 1, reserved: 0),
			new CvlSectorIndexEntry(offset: 12288, length: 4096, startLeafIndex: 2, leafCount: 1, reserved: 0)
		};

		WriteRawLayout(header, entries);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorOverlap, ex.Code);
		Assert.Contains("out-of-order", ex.Message);
	}

	[Fact]
	public unsafe void Read_SectorOverlap_ThrowsCVLF1912()
	{
		var header = CreateTwoSectorHeader();
		header.FileSize = 24576;
		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));

		var entries = new[]
		{
			new CvlSectorIndexEntry(offset: 12288, length: 4096, startLeafIndex: 1, leafCount: 1, reserved: 0),
			new CvlSectorIndexEntry(offset: 12288, length: 4096, startLeafIndex: 2, leafCount: 1, reserved: 0)
		};

		WriteRawLayout(header, entries);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorOverlap, ex.Code);
		Assert.Contains("overlaps", ex.Message);
	}

	[Fact]
	public unsafe void Read_SectorPayloadBeyondFile_ThrowsCVLF1911()
	{
		var header = CreateTwoSectorHeader();
		header.FileSize = 18432; // Sector 2's padded end (20480) lies beyond the file.
		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));

		var entries = new[]
		{
			new CvlSectorIndexEntry(offset: 12288, length: 4096, startLeafIndex: 1, leafCount: 1, reserved: 0),
			new CvlSectorIndexEntry(offset: 16384, length: 4096, startLeafIndex: 2, leafCount: 1, reserved: 0)
		};

		WriteRawLayout(header, entries);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorBoundsInvalid, ex.Code);
		Assert.Contains("extends beyond FileSize", ex.Message);
	}

	[Fact]
	public unsafe void Read_SectorOffsetBeforeSignature_ThrowsCVLF1911()
	{
		var header = CreateTwoSectorHeader();
		header.FileSize = 16384;
		CvlArchiveHeader.ExpectedMagic.CopyTo(new Span<byte>(header.Magic, 6));

		// Sector 2 offset sits inside the signature page (which ends at 8192 + 4096 = 12288).
		var entries = new[]
		{
			new CvlSectorIndexEntry(offset: 12288, length: 0, startLeafIndex: 1, leafCount: 1, reserved: 0),
			new CvlSectorIndexEntry(offset: 8192, length: 0, startLeafIndex: 2, leafCount: 1, reserved: 0)
		};

		WriteRawLayout(header, entries);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.SectorBoundsInvalid, ex.Code);
		Assert.Contains("overlaps the header/signature blocks", ex.Message);
	}

	[Fact]
	public void Read_ManifestLengthExceedsSector_ThrowsCVLF1917()
	{
		WriteFakeValidArchive(_tempFile, overrideManifestLength: 200);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.ManifestOverflowsSector, ex.Code);
	}

	[Fact]
	public void Read_EmptySector1_ThrowsCVLF1917()
	{
		WriteFakeValidArchive(_tempFile, emptySector1: true);

		var ex = Assert.Throws<CvlFormatException>(() => CvlArchiveReader.Read(_tempFile));
		Assert.Equal(CvlFormatDiagnosticIds.ManifestOverflowsSector, ex.Code);
	}

	private static CvlArchiveHeader CreateTwoSectorHeader()
	{
		return new CvlArchiveHeader
		{
			CompilerToolchainVersion = 1,
			IntendedLlvmBackendVersion = 210,
			MerkleIndexTableOffset = 4096,
			SignatureOffset = 8192,
			SectorCount = 2,
			FileSize = 24576
		};
	}
}