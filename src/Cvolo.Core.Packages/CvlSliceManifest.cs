using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cvolo.Core.Packages;

/// <summary>
/// A contiguous byte range within a specific physical sector (§1.10).
/// </summary>
/// <param name="Offset">Byte offset relative to the start of the sector's payload.</param>
/// <param name="Length">Logical length of the range in bytes.</param>
public sealed record CvlSectorRange(ulong Offset, ulong Length);

/// <summary>
/// A single target slice: the canonical LLVM triple plus the physical boundaries of its
/// compiled artifacts inside Sector 2 (AOT objects) and Sector 3 (LLVM bitcode).
/// </summary>
public sealed record CvlSliceEntry(string Triple, CvlSectorRange Sector2, CvlSectorRange Sector3);

/// <summary>
/// Parses and validates the Universal Slice Manifest (§1.10) stored at the start of Sector 1:
/// <c>[ manifest length : uint32_le ][ manifest JSON ][ layout/FFI metadata ]</c>.
/// </summary>
public sealed class CvlSliceManifest
{
	/// <summary>Maximum allowed manifest JSON size: 1 MiB.</summary>
	public const uint MaxManifestSize = 1024u * 1024u;

	/// <summary>Maximum allowed trailing layout/FFI metadata size: 8 MiB.</summary>
	public const uint MaxLayoutMetadataSize = 8u * 1024u * 1024u;

	/// <summary>The parsed, validated list of target slices.</summary>
	public IReadOnlyList<CvlSliceEntry> Slices { get; }

	/// <summary>Trailing layout / FFI metadata bytes (Sector 1 remainder, after the JSON manifest).</summary>
	public ReadOnlyMemory<byte> LayoutMetadata { get; }

	private CvlSliceManifest(IReadOnlyList<CvlSliceEntry> slices, ReadOnlyMemory<byte> layoutMetadata)
	{
		Slices = slices;
		LayoutMetadata = layoutMetadata;
	}

	/// <summary>
	/// Parses the Sector 1 payload: reads the uint32_le manifest length, extracts the JSON
	/// manifest, and validates bounds, duplicates, and overlaps per the CVLF specification.
	/// </summary>
	/// <param name="sector1Payload">The raw Sector 1 payload bytes.</param>
	/// <param name="sector2Length">Physical length of Sector 2 (bounds check for slices).</param>
	/// <param name="sector3Length">Physical length of Sector 3 (bounds check for slices).</param>
	/// <param name="baseOffset">Absolute offset of Sector 1 in the container, for accurate diagnostic offsets.</param>
	public static CvlSliceManifest Parse(
		ReadOnlySpan<byte> sector1Payload,
		ulong sector2Length,
		ulong sector3Length,
		long baseOffset = 0)
	{
		if (sector1Payload.Length < 4)
			throw new CvlFormatException(
				CvlFormatDiagnosticIds.ManifestOverflowsSector,
				baseOffset,
				"Sector 1 payload is too small to contain the manifest length prefix.");

		var manifestLength = BinaryPrimitives.ReadUInt32LittleEndian(sector1Payload[..4]);

		if (manifestLength > MaxManifestSize)
			throw new CvlFormatException(
				CvlFormatDiagnosticIds.ManifestTooLarge,
				baseOffset + 4,
				$"Slice manifest length ({manifestLength} bytes) exceeds limit ({MaxManifestSize} bytes).");

		if (4L + manifestLength > sector1Payload.Length)
			throw new CvlFormatException(
				CvlFormatDiagnosticIds.ManifestOverflowsSector,
				baseOffset + 4,
				$"Slice manifest length ({manifestLength} bytes) overflows Sector 1 payload ({sector1Payload.Length} bytes).");

		var layoutMetadataLength = sector1Payload.Length - 4L - manifestLength;
		if (layoutMetadataLength > MaxLayoutMetadataSize)
			throw new CvlFormatException(
				CvlFormatDiagnosticIds.ManifestTooLarge,
				baseOffset + 4 + manifestLength,
				$"Layout metadata length ({layoutMetadataLength} bytes) exceeds limit ({MaxLayoutMetadataSize} bytes).");

		var manifestSpan = sector1Payload.Slice(4, (int)manifestLength);
		var layoutMetadata = sector1Payload.Slice(4 + (int)manifestLength).ToArray();

		ManifestDto? dto;
		try
		{
			dto = JsonSerializer.Deserialize<ManifestDto>(manifestSpan, JsonOptions);
		}
		catch (JsonException ex)
		{
			throw new CvlFormatException(
				CvlFormatDiagnosticIds.SliceManifestMalformed,
				baseOffset + 4,
				"Slice manifest JSON parsing failed.",
				ex.Message);
		}

		if (dto is null || dto.Format != FormatId)
			throw new CvlFormatException(
				CvlFormatDiagnosticIds.SliceManifestMalformed,
				baseOffset + 4,
				"Slice manifest has an invalid or missing 'Format' identifier.");

		var slices = new List<CvlSliceEntry>();
		var seenTriples = new HashSet<string>(StringComparer.Ordinal);
		var s2Ranges = new List<CvlSectorRange>();
		var s3Ranges = new List<CvlSectorRange>();

		if (dto.Slices is not null)
		{
			foreach (var slice in dto.Slices)
			{
				if (string.IsNullOrWhiteSpace(slice.Triple))
					throw new CvlFormatException(
						CvlFormatDiagnosticIds.SliceManifestMalformed,
						baseOffset + 4,
						"A slice entry in the manifest is missing the required 'Triple' field.");

				if (!seenTriples.Add(slice.Triple))
					throw new CvlFormatException(
						CvlFormatDiagnosticIds.DuplicateSliceTriple,
						baseOffset + 4,
						$"Duplicate slice triple '{slice.Triple}' in manifest.");

				var s2 = new CvlSectorRange(slice.Sector2?.Offset ?? 0, slice.Sector2?.Length ?? 0);
				var s3 = new CvlSectorRange(slice.Sector3?.Offset ?? 0, slice.Sector3?.Length ?? 0);

				if (s2.Length > 0)
					ValidateRangeBounds(s2, sector2Length, 2, slice.Triple, baseOffset);
				if (s3.Length > 0)
					ValidateRangeBounds(s3, sector3Length, 3, slice.Triple, baseOffset);

				slices.Add(new CvlSliceEntry(slice.Triple, s2, s3));

				if (s2.Length > 0) s2Ranges.Add(s2);
				if (s3.Length > 0) s3Ranges.Add(s3);
			}
		}

		ValidateNoOverlaps(s2Ranges, 2, baseOffset);
		ValidateNoOverlaps(s3Ranges, 3, baseOffset);

		return new CvlSliceManifest(slices, layoutMetadata);
	}

	private static void ValidateRangeBounds(CvlSectorRange range, ulong sectorLength, int sectorId, string triple, long baseOffset)
	{
		if (range.Offset > sectorLength || range.Length > sectorLength - range.Offset)
			throw new CvlFormatException(
				CvlFormatDiagnosticIds.SliceOverlap,
				baseOffset + 4,
				$"Slice '{triple}' range out of bounds in Sector {sectorId}. Offset {range.Offset} + Length {range.Length} exceeds Sector Length {sectorLength}.");
	}

	private static void ValidateNoOverlaps(List<CvlSectorRange> ranges, int sectorId, long baseOffset)
	{
		if (ranges.Count <= 1) return;

		ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));

		for (var i = 1; i < ranges.Count; i++)
		{
			var previous = ranges[i - 1];
			var current = ranges[i];

			if (current.Offset < previous.Offset + previous.Length)
				throw new CvlFormatException(
					CvlFormatDiagnosticIds.SliceOverlap,
					baseOffset + 4,
					$"Overlapping slice ranges in Sector {sectorId}. Range at {current.Offset} overlaps range ending at {previous.Offset + previous.Length}.");
		}
	}

	private const string FormatId = "cvlib.slice-manifest.v1";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		PropertyNameCaseInsensitive = false
	};

	private sealed class ManifestDto
	{
		[JsonPropertyName("Format")]
		public string? Format { get; set; }

		[JsonPropertyName("Slices")]
		public List<SliceDto>? Slices { get; set; }
	}

	private sealed class SliceDto
	{
		[JsonPropertyName("Triple")]
		public string? Triple { get; set; }

		[JsonPropertyName("Sector2")]
		public RangeDto? Sector2 { get; set; }

		[JsonPropertyName("Sector3")]
		public RangeDto? Sector3 { get; set; }
	}

	private sealed class RangeDto
	{
		[JsonPropertyName("Offset")]
		public ulong Offset { get; set; }

		[JsonPropertyName("Length")]
		public ulong Length { get; set; }
	}
}