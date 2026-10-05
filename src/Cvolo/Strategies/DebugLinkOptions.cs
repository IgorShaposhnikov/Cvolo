namespace Cvolo.Strategies;

/// <summary>
/// How a link step must be told to emit debug symbols. The strategy layer receives this instead of a
/// bare flag because the switch that produces debug symbols is platform- and driver-specific:
/// MSVC/PE needs <c>/DEBUG</c> (and a program database path) whereas ELF and Mach-O need <c>-g</c>.
/// </summary>
/// <param name="Enabled">True when the user asked for debug information on this build.</param>
/// <param name="Triple">Target triple the module was generated for.</param>
/// <param name="TargetOs">Resolved target operating system used to pick the linker switch.</param>
public sealed record DebugLinkOptions(bool Enabled, string? Triple, string? TargetOs)
{
	/// <summary>No debug symbol handling requested.</summary>
	public static DebugLinkOptions None => new(false, null, null);

	/// <summary>
	/// True when the target is a Windows/MSVC target, which links through the MSVC linker and needs
	/// CodeView <c>/DEBUG</c> rather than DWARF.
	/// </summary>
	public bool IsWindowsMsvcTarget =>
		Triple is not null && Triple.EndsWith("-windows-msvc", StringComparison.Ordinal);

	/// <summary>
	/// True when the link is expected to produce a program database next to the executable.
	/// </summary>
	public bool ExpectsProgramDatabase => Enabled && IsWindowsMsvcTarget;

	/// <summary>
	/// True when the link must be followed by an out-of-band symbol bundle step (Mach-O keeps debug
	/// information in a <c>.dSYM</c> produced by <c>dsymutil</c>).
	/// </summary>
	public bool ExpectsDsymBundle =>
		Enabled
		&& !IsWindowsMsvcTarget
		&& string.Equals(TargetOs, "macos", StringComparison.OrdinalIgnoreCase);
}