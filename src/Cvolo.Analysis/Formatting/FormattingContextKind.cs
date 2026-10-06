namespace Cvolo.Analysis.Formatting;

/// <summary>
/// The call shape that directed a formatting operation. The binder classifies the call once so the
/// emitter can construct the correct formatter receiver (a bound <c>Formatter</c> value's storage for
/// <c>formatter.Write</c>, or a fresh stdout-bound formatter for the Console backend).
/// </summary>
public enum FormattingContextKind
{
	/// <summary>
	/// A value receiver of the canonical Base <c>Formatter</c> struct, member <c>Write</c>.
	/// </summary>
	FormatterWrite,
	/// <summary>
	/// The console backend (<c>Console.Write</c>) with no line terminator.
	/// </summary>
	ConsoleWrite,
	/// <summary>
	/// The console backend (<c>Console.WriteLine</c>); a newline is appended after the value.
	/// </summary>
	ConsoleWriteLine,
}
