using System.Buffers.Binary;
using System.Text;
using Cvolo.Core.Packages;

namespace Cvolo.Tests.Packages;

public sealed class CvlSliceManifestTests
{
	private static byte[] CreatePayload(string json, string trailingMetadata = "{}")
	{
		var jsonBytes = Encoding.UTF8.GetBytes(json);
		var trailingBytes = Encoding.UTF8.GetBytes(trailingMetadata);

		var payload = new byte[4 + jsonBytes.Length + trailingBytes.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)jsonBytes.Length);
		jsonBytes.CopyTo(payload, 4);
		trailingBytes.CopyTo(payload, 4 + jsonBytes.Length);

		return payload;
	}

	[Fact]
	public void Parse_ValidManifest_Succeeds_And_SeparatesMetadata()
	{
		var json = """
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [
		    {
		      "Triple": "x86_64-pc-windows-msvc",
		      "Sector2": { "Offset": 0, "Length": 100 },
		      "Sector3": { "Offset": 0, "Length": 50 }
		    }
		  ]
		}
		""";

		var trailing = """{"Structure":"Graphics"}""";
		var payload = CreatePayload(json, trailing);

		var manifest = CvlSliceManifest.Parse(payload, sector2Length: 100, sector3Length: 50, baseOffset: 4096);

		Assert.Single(manifest.Slices);
		Assert.Equal("x86_64-pc-windows-msvc", manifest.Slices[0].Triple);
		Assert.Equal(100UL, manifest.Slices[0].Sector2.Length);
		Assert.Equal(0UL, manifest.Slices[0].Sector2.Offset);
		Assert.Equal(50UL, manifest.Slices[0].Sector3.Length);

		var extractedTrailing = Encoding.UTF8.GetString(manifest.LayoutMetadata.Span);
		Assert.Equal(trailing, extractedTrailing);
	}

	[Fact]
	public void Parse_EmptySlices_IsValid()
	{
		var json = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [] }""";
		var manifest = CvlSliceManifest.Parse(CreatePayload(json), 0, 0);
		Assert.Empty(manifest.Slices);
	}

	[Fact]
	public void Parse_ZeroLengthSlices_AreAccepted()
	{
		var json = """
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [
		    { "Triple": "aarch64-apple-darwin", "Sector2": { "Offset": 0, "Length": 0 }, "Sector3": { "Offset": 0, "Length": 0 } }
		  ]
		}
		""";

		var manifest = CvlSliceManifest.Parse(CreatePayload(json), 0, 0);
		Assert.Single(manifest.Slices);
		Assert.Equal(0UL, manifest.Slices[0].Sector2.Length);
		Assert.Equal(0UL, manifest.Slices[0].Sector3.Length);
	}

	[Fact]
	public void Parse_Sector2Missing_Sector3Present_IsValid()
	{
		var json = """
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [
		    { "Triple": "win", "Sector3": { "Offset": 0, "Length": 50 } }
		  ]
		}
		""";

		var manifest = CvlSliceManifest.Parse(CreatePayload(json), sector2Length: 0, sector3Length: 50);
		Assert.Single(manifest.Slices);
		Assert.Equal(0UL, manifest.Slices[0].Sector2.Length);
		Assert.Equal(50UL, manifest.Slices[0].Sector3.Length);
	}

	[Fact]
	public void Parse_MalformedJson_ThrowsCVLF1920()
	{
		var json = """{ "Format": "cvlib.slice-manifest.v1", "Slices": [BROKEN!] }""";
		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(CreatePayload(json), 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.SliceManifestMalformed, ex.Code);
	}

	[Fact]
	public void Parse_MissingFormat_ThrowsCVLF1920()
	{
		var json = """{ "Slices": [] }""";
		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(CreatePayload(json), 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.SliceManifestMalformed, ex.Code);
	}

	[Fact]
	public void Parse_WrongFormat_ThrowsCVLF1920()
	{
		var json = """{ "Format": "something-else", "Slices": [] }""";
		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(CreatePayload(json), 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.SliceManifestMalformed, ex.Code);
	}

	[Fact]
	public void Parse_EmptyTriple_ThrowsCVLF1920()
	{
		var json = """
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [ { "Triple": "" } ]
		}
		""";
		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(CreatePayload(json), 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.SliceManifestMalformed, ex.Code);
	}

	[Fact]
	public void Parse_ZeroManifestLength_ThrowsCVLF1920()
	{
		var payload = new byte[4];
		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(payload, 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.SliceManifestMalformed, ex.Code);
	}

	[Fact]
	public void Parse_DuplicateTriple_ThrowsCVLF1914()
	{
		var json = """
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [
		    { "Triple": "x86_64-pc-windows-msvc" },
		    { "Triple": "x86_64-pc-windows-msvc" }
		  ]
		}
		""";

		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(CreatePayload(json), 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.DuplicateSliceTriple, ex.Code);
	}

	[Fact]
	public void Parse_OverlappingRanges_ThrowsCVLF1915()
	{
		var json = """
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [
		    { "Triple": "win",   "Sector2": { "Offset": 0,   "Length": 100 } },
		    { "Triple": "linux", "Sector2": { "Offset": 50,  "Length": 100 } }
		  ]
		}
		""";

		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(CreatePayload(json), 200, 0));
		Assert.Equal(CvlFormatDiagnosticIds.SliceOverlap, ex.Code);
	}

	[Fact]
	public void Parse_OutOfBoundsRange_ThrowsCVLF1915()
	{
		var json = """
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [
		    { "Triple": "win", "Sector2": { "Offset": 0, "Length": 100 } }
		  ]
		}
		""";

		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(CreatePayload(json), sector2Length: 50, sector3Length: 0));
		Assert.Equal(CvlFormatDiagnosticIds.SliceOverlap, ex.Code);
	}

	[Fact]
	public void Parse_IntegerOverflow_ThrowsCVLF1915()
	{
		var json = $$"""
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [
		    { "Triple": "win", "Sector2": { "Offset": {{ulong.MaxValue - 5}}, "Length": 10 } }
		  ]
		}
		""";

		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(CreatePayload(json), sector2Length: 100, sector3Length: 0));
		Assert.Equal(CvlFormatDiagnosticIds.SliceOverlap, ex.Code);
	}

	[Fact]
	public void Parse_ManifestTooLarge_ThrowsCVLF1916()
	{
		var payload = new byte[8];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, 2 * 1024 * 1024);

		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(payload, 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.ManifestTooLarge, ex.Code);
	}

	[Fact]
	public void Parse_ManifestLengthMaxUint_ThrowsCVLF1916_NoAllocation()
	{
		var payload = new byte[8];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, uint.MaxValue);

		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(payload, 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.ManifestTooLarge, ex.Code);
	}

	[Fact]
	public void Parse_ManifestOverflowsSector_ThrowsCVLF1917()
	{
		var payload = new byte[100];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, 100);

		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(payload, 0, 0, baseOffset: 8192));
		Assert.Equal(CvlFormatDiagnosticIds.ManifestOverflowsSector, ex.Code);
		Assert.Equal(8196, ex.Offset);
	}

	[Fact]
	public void Parse_Sector1PayloadTooSmall_ThrowsCVLF1917()
	{
		var payload = new byte[3];
		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(payload, 0, 0, baseOffset: 4096));
		Assert.Equal(CvlFormatDiagnosticIds.ManifestOverflowsSector, ex.Code);
		Assert.Equal(4096, ex.Offset);
	}

	[Fact]
	public void Parse_LayoutMetadataTooLarge_ThrowsCVLF1916()
	{
		// Manifest length is 0; trailing metadata exceeds 8 MiB limit.
		var payload = new byte[CvlSliceManifest.MaxLayoutMetadataSize + 4 + 1];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, 0);

		var ex = Assert.Throws<CvlFormatException>(() => CvlSliceManifest.Parse(payload, 0, 0));
		Assert.Equal(CvlFormatDiagnosticIds.ManifestTooLarge, ex.Code);
	}

	[Fact]
	public void Parse_SliceOrder_PreservedAsDeclared()
	{
		var json = """
		{
		  "Format": "cvlib.slice-manifest.v1",
		  "Slices": [
		    { "Triple": "x86_64-unknown-linux-gnu" },
		    { "Triple": "aarch64-apple-darwin" },
		    { "Triple": "x86_64-pc-windows-msvc" }
		  ]
		}
		""";

		var manifest = CvlSliceManifest.Parse(CreatePayload(json), 0, 0);

		Assert.Equal(3, manifest.Slices.Count);
		Assert.Equal("x86_64-unknown-linux-gnu", manifest.Slices[0].Triple);
		Assert.Equal("aarch64-apple-darwin", manifest.Slices[1].Triple);
		Assert.Equal("x86_64-pc-windows-msvc", manifest.Slices[2].Triple);
	}
}