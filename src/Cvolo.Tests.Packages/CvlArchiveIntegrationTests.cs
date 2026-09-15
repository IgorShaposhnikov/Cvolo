using System.Buffers.Binary;
using System.Text;
using Cvolo.Core.Packages;
using K4os.Compression.LZ4.Streams;
using NSec.Cryptography;

namespace Cvolo.Tests.Packages;

public sealed class CvlArchiveIntegrationTests : IDisposable
{
	private readonly string _tempDir;

	public CvlArchiveIntegrationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "cvlib_it_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { }
	}

	private string Scratch(string name) => Path.Combine(_tempDir, name);

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

	private static byte[] EncodeLz4Frame(byte[] content)
	{
		using var output = new MemoryStream();
		using (var encoder = LZ4Stream.Encode(output))
		{
			encoder.Write(content, 0, content.Length);
		}
		return output.ToArray();
	}

	private static string ManifestJson(params (string Triple, int S2Offset, int S2Len, int S3Offset, int S3Len)[] slices)
	{
		var sb = new StringBuilder();
		sb.Append("""{ "Format": "cvlib.slice-manifest.v1", "Slices": [""");
		for (var i = 0; i < slices.Length; i++)
		{
			if (i > 0) sb.Append(',');
			sb.Append($$"""{ "Triple": "{{slices[i].Triple}}", "Sector2": { "Offset": {{slices[i].S2Offset}}, "Length": {{slices[i].S2Len}} }, "Sector3": { "Offset": {{slices[i].S3Offset}}, "Length": {{slices[i].S3Len}} } }""");
		}
		sb.Append("""] }""");
		return sb.ToString();
	}

	private static Key CreateKey() => Key.Create(SignatureAlgorithm.Ed25519);

	[Fact]
	public void RoundTrip_MultiSlice_FullArchive_ReadsSlicesAndPayloads()
	{
		using var key = CreateKey();
		var s2 = new byte[64];
		var s3 = new byte[256];
		for (var i = 0; i < s2.Length; i++) s2[i] = (byte)i;
		for (var i = 0; i < s3.Length; i++) s3[i] = (byte)(255 - i);

		var manifest = ManifestJson(("x86_64-pc-windows-msvc", 0, 32, 0, 128), ("aarch64-pc-linux-gnu", 32, 32, 128, 128));
		const string source = "namespace Test; int Main() { return 42; }";

		var path = Scratch("multi.cvlib");
		var writer = new CvlArchiveWriter();
		writer.SetSector(1, CreateSector1Payload(manifest, """{"Layout":"Integration"}"""));
		writer.SetSector(2, s2);
		writer.SetSector(3, s3);
		writer.SetSourceBuffer(source);
		writer.Write(path, key);

		using var archive = CvlArchiveReader.Read(path);

		Assert.Equal(5UL, archive.Header.SectorCount);
		Assert.Equal(2, archive.Manifest.Slices.Count);
		Assert.Equal("x86_64-pc-windows-msvc", archive.Manifest.Slices[0].Triple);
		Assert.Equal("aarch64-pc-linux-gnu", archive.Manifest.Slices[1].Triple);

		var s2Payload = archive.GetSectorPayload(2);
		Assert.Equal(64, s2Payload.Length);
		for (var i = 0; i < s2.Length; i++)
			Assert.Equal((byte)i, s2Payload[i]);

		var s3Payload = archive.GetSectorPayload(3);
		Assert.Equal(256, s3Payload.Length);
		for (var i = 0; i < s3.Length; i++)
			Assert.Equal((byte)(255 - i), s3Payload[i]);

		Assert.Equal(source, archive.ReadSourceBuffer());
	}

	[Fact]
	public void Determinism_AcrossDifferentDirectories_IsBitForBitIdentical()
	{
		using var key = CreateKey();

		var pathA = Scratch(Path.Combine("subA", "pkg.cvlib"));
		var pathB = Scratch(Path.Combine("subB", "pkg.cvlib"));
		Directory.CreateDirectory(Path.GetDirectoryName(pathA)!);
		Directory.CreateDirectory(Path.GetDirectoryName(pathB)!);

		var manifest = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
		var s1 = CreateSector1Payload(manifest);
		const string source = "namespace Determinism; int Main() { return 1; }";

		var writer1 = new CvlArchiveWriter();
		writer1.SetSector(1, s1);
		writer1.SetSector(2, new byte[200]);
		writer1.SetSourceBuffer(source);
		writer1.Write(pathA, key);

		var writer2 = new CvlArchiveWriter();
		writer2.SetSector(1, s1);
		writer2.SetSector(2, new byte[200]);
		writer2.SetSourceBuffer(source);
		writer2.Write(pathB, key);

		var bytesA = File.ReadAllBytes(pathA);
		var bytesB = File.ReadAllBytes(pathB);

		Assert.Equal(bytesA.Length, bytesB.Length);
		Assert.True(bytesA.AsSpan().SequenceEqual(bytesB), "Archives written to different directories from the same inputs must be byte-for-byte identical.");
	}

	[Fact]
	public void ThinningSlice_ChangesMerkleRoot()
	{
		using var key = CreateKey();

		var pathA = Scratch("full.cvlib");
		var pathB = Scratch("thin.cvlib");

		var manifestA = ManifestJson(("win", 0, 100, 0, 0));
		var manifestB = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";

		var writerA = new CvlArchiveWriter();
		writerA.SetSector(1, CreateSector1Payload(manifestA));
		writerA.SetSector(2, new byte[100]);
		writerA.Write(pathA, key);

		var writerB = new CvlArchiveWriter();
		writerB.SetSector(1, CreateSector1Payload(manifestB));
		writerB.Write(pathB, key);

		using var archA = CvlArchiveReader.Read(pathA);
		using var archB = CvlArchiveReader.Read(pathB);

		Assert.False(archA.MerkleRootHash.SequenceEqual(archB.MerkleRootHash), "Removing a slice must change the Merkle root.");
	}

	[Fact]
	public void ReadSourceBuffer_ReturnsEmpty_WhenSector5Empty()
	{
		using var key = CreateKey();
		var path = Scratch("nosrc.cvlib");

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, CreateSector1Payload("""{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }"""));
		writer.Write(path, key);

		using var archive = CvlArchiveReader.Read(path);
		Assert.Equal(string.Empty, archive.ReadSourceBuffer());
	}

	[Fact]
	public void ReadSourceBuffer_WrongMagic_ThrowsCVLF1913()
	{
		using var key = CreateKey();
		var path = Scratch("badmagic.cvlib");

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, CreateSector1Payload("""{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }"""));
		writer.SetSector(5, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0x00, 0x00 });
		writer.Write(path, key);

		using var archive = CvlArchiveReader.Read(path);
		var ex = Assert.Throws<CvlFormatException>(() => archive.ReadSourceBuffer());
		Assert.Equal(CvlFormatDiagnosticIds.DecompressionFailed, ex.Code);
		Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ReadSourceBuffer_TruncatedFrame_ThrowsCVLF1913()
	{
		using var key = CreateKey();
		var path = Scratch("trunc.cvlib");

		var fullFrame = EncodeLz4Frame(Encoding.UTF8.GetBytes("namespace Test; int Main() { return 0; }"));
		var truncated = fullFrame.AsSpan(0, Math.Max(4, fullFrame.Length / 2)).ToArray();

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, CreateSector1Payload("""{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }"""));
		writer.SetSector(5, truncated);
		writer.Write(path, key);

		using var archive = CvlArchiveReader.Read(path);
		var ex = Assert.Throws<CvlFormatException>(() => archive.ReadSourceBuffer());
		Assert.Equal(CvlFormatDiagnosticIds.DecompressionFailed, ex.Code);
	}

	[Fact]
	public void ReadSourceBuffer_AdvertisedSizeOverLimit_ThrowsCVLF1913()
	{
		using var key = CreateKey();
		var path = Scratch("bigframe.cvlib");

		// Hand-craft an LZ4 frame header with advertised content size = 2 GiB.
		// Layout: Magic(4) FLG(1) BD(1) ContentSize(8) HC(1) = 15 bytes.
		var frame = new byte[15];
		frame[0] = 0x04; frame[1] = 0x22; frame[2] = 0x4D; frame[3] = 0x18;
		frame[4] = 0x68; // FLG: version 01, block independent, content-size present
		frame[5] = 0x40; // BD: 4 MB block max
		BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(6, 8), 2UL * 1024 * 1024 * 1024);

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, CreateSector1Payload("""{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }"""));
		writer.SetSector(5, frame);
		writer.Write(path, key);

		using var archive = CvlArchiveReader.Read(path);
		var ex = Assert.Throws<CvlFormatException>(() => archive.ReadSourceBuffer());
		Assert.Equal(CvlFormatDiagnosticIds.DecompressionFailed, ex.Code);
		Assert.Contains("Advertised", ex.Message);
	}

	[Fact]
	public void ReadSourceBuffer_NonUtf8_ThrowsCVLF1913()
	{
		using var key = CreateKey();
		var path = Scratch("noUTF8.cvlib");

		// 0xC3 0x28 is an invalid UTF-8 sequence (0x28 is not a valid continuation byte).
		var frame = EncodeLz4Frame(new byte[] { 0xC3, 0x28 });

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, CreateSector1Payload("""{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }"""));
		writer.SetSector(5, frame);
		writer.Write(path, key);

		using var archive = CvlArchiveReader.Read(path);
		var ex = Assert.Throws<CvlFormatException>(() => archive.ReadSourceBuffer());
		Assert.Equal(CvlFormatDiagnosticIds.DecompressionFailed, ex.Code);
		Assert.Contains("UTF-8", ex.Message);
	}
}
