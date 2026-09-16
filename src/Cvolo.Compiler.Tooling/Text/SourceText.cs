namespace Cvolo.Compiler.Tooling;

/// <summary>
/// An immutable snapshot of source text with line/offset mapping.
/// Lines are terminated by <c>\n</c> (CR, when present, is treated as a trailing character of the
/// previous line); line starts are indexed with <see cref="Array.BinarySearch"/> for
/// efficient <see cref="GetLinePosition(int)"/>/<see cref="GetOffset(LinePosition)"/> conversion.
/// </summary>
public sealed class SourceText
{
	private readonly string _text;
	private readonly int[] _lineStarts;

	private SourceText(string text)
	{
		_text = text;
		_lineStarts = BuildLineStarts(text);
	}

	/// <summary>
	/// Creates a <see cref="SourceText"/> from <paramref name="text"/>.
	/// Throws <see cref="ArgumentNullException"/> when <paramref name="text"/> is null.
	/// </summary>
	public static SourceText From(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return new SourceText(text);
	}

	/// <summary>
	/// The number of UTF-16 characters in the text.
	/// </summary>
	public int Length => _text.Length;

	/// <summary>
	/// Returns the UTF-16 character at the given zero-based index.
	/// </summary>
	public char this[int index] => _text[index];

	/// <summary>
	/// Returns a substring covering <paramref name="span"/>.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when the span falls outside the text.
	/// </summary>
	public string GetText(TextSpan span)
	{
		if (span.Start < 0 || span.Start > _text.Length)
			throw new ArgumentOutOfRangeException(nameof(span));

		if (span.Length < 0 || span.Start + span.Length > _text.Length)
			throw new ArgumentOutOfRangeException(nameof(span));

		return _text.Substring(span.Start, span.Length);
	}

	/// <summary>
	/// Converts a <see cref="LinePosition"/> to a zero-based character offset.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when the line does not exist or the
	/// character exceeds the line's length (the end-of-line position is inclusive).
	/// </summary>
	public int GetOffset(LinePosition position)
	{
		if (position.Line < 0 || position.Line >= _lineStarts.Length)
			throw new ArgumentOutOfRangeException(nameof(position));

		var lineStart = _lineStarts[position.Line];
		var lineEnd = position.Line + 1 < _lineStarts.Length
			? _lineStarts[position.Line + 1]
			: _text.Length;

		if (position.Character < 0 || position.Character > lineEnd - lineStart)
			throw new ArgumentOutOfRangeException(nameof(position));

		return lineStart + position.Character;
	}

	/// <summary>
	/// Converts a zero-based character offset to a <see cref="LinePosition"/>.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when the offset is outside the text.
	/// </summary>
	public LinePosition GetLinePosition(int offset)
	{
		if (offset < 0 || offset > _text.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		var line = Array.BinarySearch(_lineStarts, offset);
		if (line < 0)
			line = ~line - 1;

		var character = offset - _lineStarts[line];
		return new LinePosition(line, character);
	}

	/// <summary>
	/// Returns the underlying text.
	/// </summary>
	public override string ToString() => _text;

	private static int[] BuildLineStarts(string text)
	{
		var starts = new List<int> { 0 };

		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] == '\n')
				starts.Add(i + 1);
		}

		return starts.ToArray();
	}
}
