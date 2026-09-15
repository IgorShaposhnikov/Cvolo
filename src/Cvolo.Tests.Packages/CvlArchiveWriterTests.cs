using System.Buffers.Binary;
using System.Text;
using Blake3;
using Cvolo.Core.Packages;
using K4os.Compression.LZ4.Streams;
using NSec.Cryptography;

namespace Cvolo.Tests.Packages;

public sealed class CvlArchiveWriterTests : IDisposable
{
	private readonly string _tempFile;

	public CvlArchiveWriterTests()
	{
		_tempFile = Path.GetTempFileName();
	}

	public void Dispose()
	{
		if (File.Exists(_tempFile))
			File.Delete(_tempFile);
	}

	private static byte[] CreateSector1Payload(string json, string trailingMetadata = "{}")
	{
		var jsonBytes = Encoding.UTF8.GetBytes(json);
		var trailingBytes = Encoding.UTF8.GetBytes(trailingMetadata);

		var payload = new byte[4 + jsonBytes.Length + trailingBytes.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)jsonBytes.Length);
		jsonBytes.CopyTo(payload, 4);
		trailingBytes.CopyTo(payload, 4 + jsonBytes.Length);

		return payload;
	}

	private static (CvlArchiveHeader Header, CvlSectorIndexEntry[] Entries, byte[] Bytes) ReadArchive(string path)
	{
		var bytes = File.ReadAllBytes(path);

		var header = CvlArchiveHeader.ReadFrom(bytes.AsSpan(0, 80));
		var entries = new CvlSectorIndexEntry[header.SectorCount];
		for (var i = 0; i < (int)header.SectorCount; i++)
			entries[i] = CvlSectorIndexEntry.ReadFrom(bytes.AsSpan((int)header.MerkleIndexTableOffset + i * 32, 32));

		return (header, entries, bytes);
	}

	[Fact]
	public void Write_EmitsCanonicalLayout_IndexTableSizeReflectedInSignatureOffset()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		var writer = new CvlArchiveWriter();
		var manifestJson = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
		writer.SetSector(1, CreateSector1Payload(manifestJson));
		writer.Write(_tempFile, key);

		var (header, entries, _) = ReadArchive(_tempFile);

		// §1.6: MerkleIndexTableOffset=4096, SignatureOffset=4096+align_up(sectorCount*32, 4096)
		Assert.Equal(4096UL, header.MerkleIndexTableOffset);
		Assert.Equal(8192UL, header.SignatureOffset); // 5 sectors * 32 = 160 -> aligns to 4096

		// Canonical container: always Sectors 1..5 (§1.8)
		Assert.Equal(5UL, header.SectorCount);

		// First sector payload begins after the signature block page.
		Assert.Equal(12288UL, entries[0].Offset);

		// Sector 1 (short manifest) occupies exactly one aligned page, so Sector 2 sits 4096 bytes later.
		Assert.Equal(entries[0].Offset + 4096, entries[1].Offset);

		// FileSize must be page-aligned and equal the physical length.
		var physical = new FileInfo(_tempFile).Length;
		Assert.Equal((ulong)physical, header.FileSize);
		Assert.Equal(0UL, header.FileSize % 4096);
	}

	[Fact]
	public unsafe void Write_Sector5000Bytes_MultipageLeavesAndIndex()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		var writer = new CvlArchiveWriter();
		var manifestJson = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
		writer.SetSector(1, CreateSector1Payload(manifestJson));

		var sector2 = new byte[5000];
		sector2[0] = 0xAA;
		sector2[4999] = 0xBB;
		writer.SetSector(2, sector2);

		writer.Write(_tempFile, key);

		var (header, entries, bytes) = ReadArchive(_tempFile);

		// 5000 bytes -> ceil(5000/4096) = 2 leaves (§1.4)
		var s2 = entries[1];
		Assert.Equal(5000UL, s2.Length);
		Assert.Equal(2U, s2.LeafCount);
		Assert.Equal(2U, s2.StartLeafIndex); // leaf 0 = header, leaf 1 = sector 1 page, sector 2 starts at leaf 2

		// Sector 3 (empty here) follows the 2 pages of sector 2.
		Assert.Equal(4U, entries[2].StartLeafIndex);
		Assert.Equal(1U, entries[2].LeafCount);

		// Layout: sector2 spans 8192 aligned bytes.
		var s2Aligned = (5000UL + 4095) & ~(ulong)4095;
		Assert.Equal(s2.Offset + s2Aligned, entries[2].Offset);

		// Verify the padded leaf: BLAKE3(904 payload bytes || 3192 zero bytes) participates in the tree.
		var paddedPage = new byte[4096];
		Array.Copy(bytes, (int)s2.Offset + 4096, paddedPage, 0, 904);
		var expectedLeaf = Hasher.Hash(paddedPage).AsSpan().ToArray();

		// The full Merkle root is over [header, s1 page, s2 page1, s2 page2, ...];
		// recompute it to confirm the writer's root matches the spec.
		var leaves = new List<byte[]>
		{
			Hasher.Hash(CreateHeaderLeafBytes(header)).AsSpan().ToArray(),
			Hasher.Hash(bytes.AsSpan((int)entries[0].Offset, 4096)).AsSpan().ToArray(),
			Hasher.Hash(bytes.AsSpan((int)s2.Offset, 4096)).AsSpan().ToArray(),
			expectedLeaf,
			ComputeEmptyLeaf(3),
			ComputeEmptyLeaf(4),
			ComputeEmptyLeaf(5)
		};

		Assert.True(ComputeRoot(leaves).AsSpan().SequenceEqual(new ReadOnlySpan<byte>(header.MerkleRootHash, 32)), "Writer Merkle root must match a from-scratch BLAKE3 tree over the emitted bytes.");
	}

	[Fact]
	public void Write_SectorLengthMultipleOfPageSize_NoExtraPage()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		var writer = new CvlArchiveWriter();
		var manifestJson = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
		writer.SetSector(1, CreateSector1Payload(manifestJson));

		var sector2 = new byte[8192];
		writer.SetSector(2, sector2);
		writer.Write(_tempFile, key);

		var (_, entries, _) = ReadArchive(_tempFile);
		Assert.Equal(2U, entries[1].LeafCount); // exactly 8192/4096, no third page
		Assert.Equal(4U, entries[2].StartLeafIndex); // 1 (header) + 1 (sector 1) + 2 (sector 2)
	}

	[Fact]
	public unsafe void Write_EmptySector5_ShiftsSourceToSector4DomainSeparation()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);

		var pathA = Path.GetTempFileName();
		var pathB = Path.GetTempFileName();
		try
		{
			var manifestJson = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
			var payload = new byte[100];
			payload[0] = 0xFF;

			var writerA = new CvlArchiveWriter();
			writerA.SetSector(1, CreateSector1Payload(manifestJson));
			writerA.SetSector(2, payload);
			writerA.Write(pathA, key);

			var writerB = new CvlArchiveWriter();
			writerB.SetSector(1, CreateSector1Payload(manifestJson));
			writerB.SetSector(3, payload);
			writerB.Write(pathB, key);

			var (hA, _, _) = ReadArchive(pathA);
			var (hB, _, _) = ReadArchive(pathB);

			// Moving the same payload between sectors yields different domain-separated leaves -> different roots.
			var rootA = new ReadOnlySpan<byte>(hA.MerkleRootHash, 32);
			var rootB = new ReadOnlySpan<byte>(hB.MerkleRootHash, 32);
			Assert.False(rootA.SequenceEqual(rootB));
		}
		finally
		{
			if (File.Exists(pathA)) File.Delete(pathA);
			if (File.Exists(pathB)) File.Delete(pathB);
		}
	}

	[Fact]
	public void Write_Sector5SourceBuffer_StartsWithLz4FrameMagic()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		var writer = new CvlArchiveWriter();
		var manifestJson = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
		writer.SetSector(1, CreateSector1Payload(manifestJson));
		writer.SetSourceBuffer("int main() { return 42; }");
		writer.Write(_tempFile, key);

		var (_, entries, bytes) = ReadArchive(_tempFile);

		var s5 = entries[4];
		Assert.True(s5.Length > 0, "Sector 5 must carry the compressed source buffer.");
		Assert.Equal(0x04, bytes[s5.Offset]);
		Assert.Equal(0x22, bytes[s5.Offset + 1]);
		Assert.Equal(0x4D, bytes[s5.Offset + 2]);
		Assert.Equal(0x18, bytes[s5.Offset + 3]);
	}

	[Fact]
	public void Write_SourceBuffer_RoundTripsThroughLz4()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		var writer = new CvlArchiveWriter();
		const string sourceText = "namespace Test; int Main() { return 100; }";
		var manifestJson = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
		writer.SetSector(1, CreateSector1Payload(manifestJson));
		writer.SetSourceBuffer(sourceText);
		writer.Write(_tempFile, key);

		var (_, entries, bytes) = ReadArchive(_tempFile);
		var s5 = entries[4];

		using var ms = new MemoryStream(bytes, (int)s5.Offset, (int)s5.Length);
		using var lz4 = LZ4Stream.Decode(ms);
		using var reader = new StreamReader(lz4, Encoding.UTF8);
		Assert.Equal(sourceText, reader.ReadToEnd());
	}

	[Fact]
	public void Write_Deterministic_TwoIdenticalBuildsProduceIdenticalBytes()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);

		var path1 = Path.GetTempFileName();
		var path2 = Path.GetTempFileName();
		try
		{
			var manifestJson = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
			var s1 = CreateSector1Payload(manifestJson);
			var s2 = new byte[100];
			s2[50] = 0x42;

			var low = new CvlArchiveWriter { CompilerToolchainVersion = 1, IntendedLlvmBackendVersion = 210 };
			low.SetSector(1, s1);
			low.SetSector(2, s2);
			low.SetSourceBuffer("string GetName() { return \"Determinism\"; }");
			low.Write(path1, key);

			var high = new CvlArchiveWriter { CompilerToolchainVersion = 1, IntendedLlvmBackendVersion = 210 };
			high.SetSector(1, s1);
			high.SetSector(2, s2);
			high.SetSourceBuffer("string GetName() { return \"Determinism\"; }");
			high.Write(path2, key);

			var bytes1 = File.ReadAllBytes(path1);
			var bytes2 = File.ReadAllBytes(path2);

			Assert.Equal(bytes1.Length, bytes2.Length);
			Assert.True(bytes1.AsSpan().SequenceEqual(bytes2), "Two writers with identical inputs must produce bit-for-bit identical containers.");
		}
		finally
		{
			if (File.Exists(path1)) File.Delete(path1);
			if (File.Exists(path2)) File.Delete(path2);
		}
	}

	[Fact]
	public void Write_MissingSector1_ThrowsInvalidOperationException()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		var writer = new CvlArchiveWriter();
		Assert.Throws<InvalidOperationException>(() => writer.Write(_tempFile, key));
	}

	[Fact]
	public void Write_EmptySector1_ThrowsInvalidOperationException()
	{
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		var writer = new CvlArchiveWriter();
		writer.SetSector(1, ReadOnlyMemory<byte>.Empty);
		Assert.Throws<InvalidOperationException>(() => writer.Write(_tempFile, key));
	}

	[Fact]
	public void SetSector_RejectsOutOfRangeIds()
	{
		var writer = new CvlArchiveWriter();
		Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetSector(0, new byte[1]));
		Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetSector(65, new byte[1]));
	}

	[Fact]
	public void EmptyLeaves_DontCollide_WithZeroPages()
	{
		// empty_leaf(sector_id) = BLAKE3( 0x00 0x00 0x00 0x00 || uint32_le(sector_id) )
		byte[] Compute(int id)
		{
			var marker = new byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(marker.AsSpan(4, 4), (uint)id);
			return Hasher.Hash(marker).AsSpan().ToArray();
		}

		var zeroPage = Hasher.Hash(new byte[4096]).AsSpan().ToArray();
		var remainders = 0;

		for (var id = 1; id <= 5; id++)
		{
			var leaf = Compute(id);
			Assert.False(leaf.AsSpan().SequenceEqual(zeroPage));
			if (leaf.AsSpan().SequenceEqual(Compute(id + 1))) remainders++;
		}
		Assert.Equal(0, remainders);
	}

	private static byte[] CreateHeaderLeafBytes(CvlArchiveHeader header)
	{
		var bytes = new byte[80];
		header.WriteTo(bytes);
		Array.Clear(bytes, 32, 32); // MerkleRootHash normalized to zero for leaf_0 (§1.4)
		return bytes;
	}

	private static byte[] ComputeEmptyLeaf(uint sectorId)
	{
		var marker = new byte[8];
		BinaryPrimitives.WriteUInt32LittleEndian(marker.AsSpan(4, 4), sectorId);
		return Hasher.Hash(marker).AsSpan().ToArray();
	}

	private static byte[] ComputeRoot(List<byte[]> leaves)
	{
		while (leaves.Count > 1)
		{
			var next = new List<byte[]>((leaves.Count + 1) / 2);
			for (var i = 0; i < leaves.Count; i += 2)
			{
				if (i + 1 < leaves.Count)
				{
					var concat = new byte[64];
					leaves[i].CopyTo(concat, 0);
					leaves[i + 1].CopyTo(concat, 32);
					next.Add(Hasher.Hash(concat).AsSpan().ToArray());
				}
				else
				{
					next.Add(leaves[i]);
				}
			}
			leaves = next;
		}
		return leaves[0];
	}
}