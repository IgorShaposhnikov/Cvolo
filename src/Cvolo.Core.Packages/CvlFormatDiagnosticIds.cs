namespace Cvolo.Core.Packages;

/// <summary>
/// Stable identifiers for .cvlib container format diagnostics.
/// Range: CVLF1900 - CVLF1999, reserved exclusively for the container format.
/// Language-level diagnostics continue to use the CVLxxxx prefix.
/// </summary>
public static class CvlFormatDiagnosticIds
{
	/// <summary>Malformed container header or magic (bad magic, non-zero reserved, size &lt; 4096).</summary>
	public const string HeaderMalformed = "CVLF1900";

	/// <summary>Universal alias target could not be resolved for platform (host triple not in manifest, or slice Length == 0).</summary>
	public const string MissingTargetSlice = "CVLF1901";

	/// <summary>Unsigned or tampered bitcode; Ed25519 or Merkle mismatch during dev (falls back to source).</summary>
	public const string BitcodeTampered = "CVLF1902";

	/// <summary>Modification blocked; Sector 5 is read-only (LSP write attempt).</summary>
	public const string Sector5ReadOnly = "CVLF1903";

	/// <summary>Unsigned/corrupt package cannot fall back because source was stripped (--strip-source).</summary>
	public const string FatalStripSource = "CVLF1904";

	/// <summary>codesign not found; required for macOS ad-hoc signing of dev-bundle sidecars.</summary>
	public const string CodesignNotFound = "CVLF1905";

	/// <summary>ABI collision on soname; parallel load refused.</summary>
	public const string AbiCollision = "CVLF1906";

	/// <summary>Namespace limit reached; best-effort file-clone isolation applied (DL_NNS exhausted).</summary>
	public const string NamespaceExhausted = "CVLF1907";

	/// <summary>Callback Guard caught a panic on a foreign thread; application aborting.</summary>
	public const string CallbackGuardPanic = "CVLF1908";

	/// <summary>Callback Guard escalated to hard terminate on an unchecked host.</summary>
	public const string CallbackGuardEscalate = "CVLF1909";

	/// <summary>Container size exceeds limit or header FileSize mismatches physical file length.</summary>
	public const string FileSizeMismatch = "CVLF1910";

	/// <summary>Sector offset/length out of bounds; index table inconsistent with FileSize, or SectorCount over limit.</summary>
	public const string SectorBoundsInvalid = "CVLF1911";

	/// <summary>Overlapping or mis-ordered sectors in the index table.</summary>
	public const string SectorOverlap = "CVLF1912";

	/// <summary>Sector 5 decompression failed or LZ4 header malformed.</summary>
	public const string DecompressionFailed = "CVLF1913";

	/// <summary>Duplicate slice triple in manifest.</summary>
	public const string DuplicateSliceTriple = "CVLF1914";

	/// <summary>Slice range out of bounds or overlapping.</summary>
	public const string SliceOverlap = "CVLF1915";

	/// <summary>Slice manifest length exceeds limit, or layout metadata exceeds limit.</summary>
	public const string ManifestTooLarge = "CVLF1916";

	/// <summary>Slice manifest length overflows the physical payload of Sector 1.</summary>
	public const string ManifestOverflowsSector = "CVLF1917";

	/// <summary>Release build requires a slice for the host triple; none present.</summary>
	public const string ReleaseMissingHostSlice = "CVLF1918";

	/// <summary>Release build cannot fall back to source recompilation.</summary>
	public const string ReleaseCannotFallback = "CVLF1919";

	/// <summary>Slice manifest JSON malformed, invalid, or missing required fields.</summary>
	public const string SliceManifestMalformed = "CVLF1920";
}