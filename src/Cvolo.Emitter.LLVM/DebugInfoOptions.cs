namespace Cvolo.Emitter.LLVM;

/// <summary>
/// Configuration for one module's debug-information emission. The emitter only allocates a
/// <see cref="DebugMetadata"/> instance when <see cref="Enabled"/> is <see langword="true"/>, so a
/// build without <c>--debug</c> performs no debug work at all.
/// </summary>
public sealed record DebugInfoOptions
{
	/// <summary>Whether debug information should be emitted for this module.</summary>
	public bool Enabled { get; init; }

	/// <summary>Value recorded in the <c>DICompileUnit</c> producer field.</summary>
	public string Producer { get; init; } = "Cvolo";

	/// <summary>
	/// Whether the module is produced by an optimizing pipeline. Recorded as the compile unit's
	/// <c>isOptimized</c> flag.
	/// </summary>
	public bool IsOptimized { get; init; }

	/// <summary>
	/// Source file used as the compile unit's primary <c>DIFile</c>, normally the entry file of the
	/// project (never a base/system SDK source). When null, a placeholder name is used.
	/// </summary>
	public string? PrimarySourcePath { get; init; }
}