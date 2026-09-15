namespace Cvolo.Packaging;

/// <summary>
/// Stable identifiers for package-manager diagnostics.
/// Range: CVLP3000 - CVLP3099 (reserved exclusively for the local package manager).
/// Container-format diagnostics keep the CVLF19xx prefix; language diagnostics keep CVLxxxx.
/// </summary>
public static class PackageDiagnosticIds
{
	/// <summary>Error: .cvlproj is missing the required &lt;PackageId&gt; property.</summary>
	public const string MissingPackageId = "CVLP3000";

	/// <summary>Error: .cvlproj is missing the required &lt;Version&gt; property.</summary>
	public const string MissingVersion = "CVLP3001";

	/// <summary>Error: the &lt;Version&gt; value is not valid SemVer 2.0.</summary>
	public const string InvalidSemVer = "CVLP3002";

	/// <summary>Error: no slice for the host triple is present in the .cvlib.</summary>
	public const string MissingHostSlice = "CVLP3010";

	/// <summary>Error: the package ID does not match the target cache directory.</summary>
	public const string PackageIdMismatch = "CVLP3011";

	/// <summary>Error: same package + version already cached with a different content hash.</summary>
	public const string CachedContentMismatch = "CVLP3012";

	/// <summary>Error: two transitive dependencies require incompatible version ranges.</summary>
	public const string VersionConflict = "CVLP3020";

	/// <summary>Error: cvolo.lock.json is out of sync with .cvlproj; run 'cvolo pkg install'.</summary>
	public const string LockOutOfSync = "CVLP3030";

	/// <summary>Error: the package is unsigned and the trusted-keys policy rejects it.</summary>
	public const string UnsignedRejected = "CVLP3040";

	/// <summary>Warning: the package is unsigned; installing without verification.</summary>
	public const string UnsignedWarning = "CVLP3050";
}
