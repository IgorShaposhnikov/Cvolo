namespace Cvolo.Compiler.Tooling;

/// <summary>
/// An immutable, validated half-open span of <see cref="SourceText"/> characters [<see cref="Start"/>, <see cref="End"/>).
/// The constructor validates that both bounds are non-negative and that the span does not overflow.
/// </summary>
public readonly record struct TextSpan
{
	/// <summary>
	/// The zero-based index of the first character of the span.
	/// </summary>
	public int Start { get; }
	/// <summary>
	/// The number of characters in the span.
	/// </summary>
	public int Length { get; }
	/// <summary>
	/// The exclusive index one past the last character of the span.
	/// </summary>
	public int End => Start + Length;

	/// <summary>
	/// Creates a span of <paramref name="length"/> characters starting at <paramref name="start"/>.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="start"/> is negative, or
	/// <paramref name="length"/> is negative or causes the span to overflow.
	/// </summary>
	public TextSpan(int start, int length)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(start);

		if (length < 0 || start > int.MaxValue - length)
		{
			throw new ArgumentOutOfRangeException(nameof(length));
		}

		Start = start;
		Length = length;
	}
}
