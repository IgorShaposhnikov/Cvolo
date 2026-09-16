namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Tooling-owned diagnostic severity levels, independent of compiler-internal severities.
/// The compiler currently produces <see cref="Error"/> and <see cref="Warning"/> only.
/// </summary>
public enum DiagnosticSeverity
{
	/// <summary>
	/// An error that prevents or invalidates compilation.
	/// </summary>
	Error,
	/// <summary>
	/// A warning that does not prevent compilation.
	/// </summary>
	Warning,
	/// <summary>
	/// Informational notification.
	/// </summary>
	Info,
	/// <summary>
	/// A suggestion or stylistic hint.
	/// </summary>
	Hint
}
