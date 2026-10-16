using Cvolo.Projects;

namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Identifies the frozen external unit kind in a <see cref="FrozenUniverseCapture"/>.
/// </summary>
public enum CapturedExternalUnitKind
{
	ExternalPackageApi,
	PackageTemplate,
}

/// <summary>
/// Immutable description of a project universe capture request.
/// </summary>
/// <param name="ProjectPath">Path to the project file (<c>.cvlproj</c>) or a directory containing one.</param>
/// <param name="SourceOverrides">Authoritative in-memory text (editor overlays) keyed by full source path.</param>
/// <param name="LibraryPaths">Explicit library roots supplied by the host.</param>
/// <param name="RequestedTarget">Requested target descriptor. <see langword="null"/> or a host/default value is supported by P1.</param>
/// <param name="AllowRestore">
/// When <see langword="true"/>, a manifest-backed project may restore missing packages while capturing.
/// Defaults to <see langword="false"/> so an incremental capture never triggers package restore as a
/// side effect; callers that want capture-time materialization opt in explicitly.
/// </param>
public sealed record ToolingCaptureRequest(
	string ProjectPath,
	IReadOnlyDictionary<string, string>? SourceOverrides = null,
	IReadOnlyList<string>? LibraryPaths = null,
	string? RequestedTarget = null,
	bool AllowRestore = false);

/// <summary>
/// The provenance class of a captured source file. Tooling-owned so the public capture API does
/// not leak compiler-internal types.
/// </summary>
public enum CapturedSourceOrigin
{
	BaseSdk,
	SystemSdk,
	Project,
	Package,
}

/// <summary>
/// One fully materialized source file inside a frozen capture.
/// </summary>
public sealed record CapturedSource(string Path, string Text, CapturedSourceOrigin Origin);

/// <summary>
/// One fully materialized external semantic unit (package API or package template) inside a frozen capture.
/// </summary>
public sealed record CapturedExternalUnit(
	string FilePath,
	string SourceText,
	CapturedExternalUnitKind Kind,
	string? PackageId,
	string? PackageVersion,
	IReadOnlyDictionary<string, CapturedDefaultabilityCertificate>? DefaultabilityCertificates = null);

/// <summary>
/// Tooling-owned projection of an imported type's defaultability certificate. Mirrors the producer
/// verdict carried in package metadata (state/reason/fingerprint) without leaking packaging types
/// across the compiler/tooling boundary.
/// </summary>
public sealed record CapturedDefaultabilityCertificate(string State, string? Reason, string? Fingerprint);

/// <summary>
/// One fully materialized package source document inside a frozen capture.
/// </summary>
public sealed record CapturedPackageSource(
	string PackageId,
	string Version,
	string RelativePath,
	string FilePath,
	string Source);

/// <summary>
/// A project-definition or lock file captured verbatim so the digest covers the manifest identity.
/// </summary>
public sealed record CapturedFile(string Path, string Text);

/// <summary>
/// The complete, immutable set of bytes and provenance a project universe was built from. A
/// <see cref="BuildResult"/> may be produced from a capture without touching the file system again.
/// </summary>
public sealed record FrozenUniverseCapture(
	int SchemaVersion,
	string ProjectPath,
	string TargetAndOptionsFingerprint,
	IReadOnlyList<CapturedSource> Sources,
	IReadOnlyList<CapturedFile> ProjectAndLockFiles,
	IReadOnlyList<CapturedExternalUnit> ExternalUnits,
	IReadOnlyList<CapturedPackageSource> PackageSources,
	IReadOnlyList<string> BaseSourceFiles,
	string CaptureDigest)
{
	/// <summary>
	/// The capture schema version understood by this build of the tooling.
	/// </summary>
	public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Why a capture could not produce a complete frozen universe.
/// </summary>
public enum CaptureUnavailableReason
{
	UnsupportedTarget,
	UnsupportedConfiguration,
	UnmaterializedDependency,
	InconsistentExternalObservation,
	CaptureUnstable,
	UncapturedInput,
}

/// <summary>
/// Why a capture failed outright.
/// </summary>
public enum CaptureFailureReason
{
	DiscoveryFailed,
	ReadFailed,
}

/// <summary>
/// The outcome of <see cref="CvoloWorkspace.CaptureUniverseV1"/>.
/// </summary>
public abstract record CaptureResult
{
	private CaptureResult()
	{
	}

	/// <summary>A complete frozen universe was materialized.</summary>
	public sealed record Captured(FrozenUniverseCapture Capture) : CaptureResult;

	/// <summary>The universe is complete in principle but cannot be produced right now.</summary>
	public sealed record Unavailable(CaptureUnavailableReason Reason, IReadOnlyList<string> MissingInputs) : CaptureResult;

	/// <summary>The capture could not be produced because of an unrecoverable error.</summary>
	public sealed record Failed(CaptureFailureReason Reason, string Detail) : CaptureResult;
}

/// <summary>
/// The outcome of <see cref="CvoloWorkspace.BuildSnapshotFromCaptureV1"/>.
/// </summary>
public abstract record BuildResult
{
	private BuildResult()
	{
	}

	/// <summary>A generation-owned snapshot was built from the capture with no further input reads.</summary>
	public sealed record Ready(
		CvoloProject Project,
		ProjectSnapshot Snapshot,
		string ResolvedClosureFingerprint,
		string CaptureDigest) : BuildResult;

	/// <summary>The capture is structurally valid but this build cannot serve it (for example a schema mismatch).</summary>
	public sealed record Unavailable(string Reason) : BuildResult;

	/// <summary>The build failed.</summary>
	public sealed record Failed(string Reason, string CaptureDigest) : BuildResult;
}
