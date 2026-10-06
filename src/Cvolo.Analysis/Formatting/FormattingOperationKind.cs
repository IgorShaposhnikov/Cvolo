namespace Cvolo.Analysis.Formatting;

/// <summary>
/// The canonical formatting operation bound for a value. One shared resolver produces these for every
/// formatting consumer (Console output, <c>Formatter.Write</c>, formatter interpolation segments), and
/// the emitter consumes the already-bound operation instead of re-deriving member selection.
/// </summary>
public enum FormattingOperationKind
{
	/// <summary>
	/// A primitive value formatted canonically (integers, bool, char, floats).
	/// </summary>
	Primitive,
	/// <summary>
	/// A <see langword="string"/> value written through the formatter sink.
	/// </summary>
	String,

	/// <summary>
	/// Explicit user conformance: the value's <c>Format(refvar Formatter)</c> is invoked.
	/// </summary>
	IFormattable,
	/// <summary>
	/// Explicit user conformance: the value's <c>ToString()</c> result is written as a string.
	/// </summary>
	IStringable,
	/// <summary>
	/// Canonical <c>Option</c> composite formatting (<c>Some(&lt;T&gt;)</c> / <c>None</c>).
	/// </summary>
	Option,
	/// <summary>
	/// Canonical <c>Result</c> composite formatting (<c>Ok(&lt;T&gt;)</c> / <c>Err(&lt;E&gt;)</c>).
	/// </summary>
	Result,
}
