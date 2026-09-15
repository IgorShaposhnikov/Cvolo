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

	/// <summary>Error: the package ID does not match the target cache directory.</summary>
	public const string PackageIdMismatch = "CVLP3011";

	/// <summary>Error: same package + version already cached with a different content hash.</summary>
	public const string CachedContentMismatch = "CVLP3012";

	/// <summary>Error: a package artifact range is too large for the current extraction implementation.</summary>
	public const string ArtifactRangeTooLarge = "CVLP3013";

	/// <summary>Error: a cached .cvlib is not thinned to exactly the host target as required by cvlib v0.2.6.</summary>
	public const string CacheNotThinned = "CVLP3014";

	/// <summary>Error: the selected host slice has no Sector 2 native object for the current non-LTO build path.</summary>
	public const string NativeObjectUnavailable = "CVLP3015";

	/// <summary>Error: two transitive dependencies require incompatible version ranges.</summary>
	public const string VersionConflict = "CVLP3020";

	/// <summary>Error: the project does not reference the requested package.</summary>
	public const string PackageNotReferenced = "CVLP3021";

	/// <summary>Error: cvolo.lock.json is out of sync with .cvlproj; run 'cvolo pkg install'.</summary>
	public const string LockOutOfSync = "CVLP3030";

	/// <summary>Error: cached package content recorded at install time does not match the lock file during build.</summary>
	public const string BuildCacheContentMismatch = "CVLP3032";

}
