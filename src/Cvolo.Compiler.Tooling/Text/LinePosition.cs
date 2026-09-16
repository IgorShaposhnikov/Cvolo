namespace Cvolo.Compiler.Tooling;

/// <summary>
/// A zero-based line and character position within a <see cref="SourceText"/>.
/// Both components are validated to be non-negative.
/// </summary>
public readonly record struct LinePosition
{
	/// <summary>
	/// The zero-based line number.
	/// </summary>
	public int Line { get; }
	/// <summary>
	/// The zero-based character index within <see cref="Line"/>.
	/// </summary>
	public int Character { get; }

	/// <summary>
	/// Creates a position at the given <paramref name="line"/> and <paramref name="character"/>.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when either component is negative.
	/// </summary>
	public LinePosition(int line, int character)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(line);
		ArgumentOutOfRangeException.ThrowIfNegative(character);

		Line = line;
		Character = character;
	}
}
