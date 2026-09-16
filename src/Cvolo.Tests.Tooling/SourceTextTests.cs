using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class SourceTextTests
{
	[Fact]
	public void From_Null_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>(() => SourceText.From(null!));
	}

	[Fact]
	public void EmptyDocument_LengthIsZero_AndOffsetRoundTrips()
	{
		var text = SourceText.From("");

		Assert.Equal(0, text.Length);
		Assert.Equal(new LinePosition(0, 0), text.GetLinePosition(0));
		Assert.Equal(0, text.GetOffset(text.GetLinePosition(0)));
		Assert.Equal("", text.GetText(new TextSpan(0, 0)));

		Assert.Throws<ArgumentOutOfRangeException>(() => text.GetLinePosition(1));
		Assert.Throws<ArgumentOutOfRangeException>(() => text.GetLinePosition(-1));
	}

	[Theory]
	[InlineData("abc")]
	[InlineData("abc\n")]
	[InlineData("abc\r\n")]
	[InlineData("abc\ndef")]
	[InlineData("abc\r\ndef\r\n")]
	[InlineData("hello\r\nworld\nlast")]
	[InlineData("a\r\nb\nc\r\nd\r")]
	[InlineData("line1\nline2\nline3\n")]
	[InlineData("a\tb\tc")]
	[InlineData("\n")]
	[InlineData("x")]
	[InlineData("")]
	public void OffsetToLinePositionToOffset_RoundTrips_ForEveryOffset(string source)
	{
		var text = SourceText.From(source);

		for (var offset = 0; offset <= text.Length; offset++)
		{
			var position = text.GetLinePosition(offset);
			Assert.Equal(offset, text.GetOffset(position));
		}
	}

	[Fact]
	public void LinePositions_TrackLineBoundaries_ForMixedEndings()
	{
		var text = SourceText.From("ab\r\ncd\nef");

		Assert.Equal(new LinePosition(0, 0), text.GetLinePosition(0));
		Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(2));
		Assert.Equal(new LinePosition(1, 0), text.GetLinePosition(4));
		Assert.Equal(new LinePosition(1, 2), text.GetLinePosition(6));
		Assert.Equal(new LinePosition(2, 0), text.GetLinePosition(7));
		Assert.Equal(new LinePosition(2, 2), text.GetLinePosition(9));
	}

	[Fact]
	public void GetOffset_Throws_WhenLineIsOutsideSource()
	{
		var text = SourceText.From("abc\ndef");

		Assert.Throws<ArgumentOutOfRangeException>(() => text.GetOffset(new LinePosition(2, 0)));
	}

	[Fact]
	public void GetOffset_Throws_WhenCharacterExceedsSelectedLine()
	{
		var text = SourceText.From("abc");

		Assert.Equal(3, text.GetOffset(new LinePosition(0, 3)));
		Assert.Throws<ArgumentOutOfRangeException>(() => text.GetOffset(new LinePosition(0, 4)));
	}

	[Fact]
	public void GetText_ReturnsExactSlices_ForAllEndingStyles()
	{
		var text = SourceText.From("ab\r\ncd\nef");

		Assert.Equal("ab", text.GetText(new TextSpan(0, 2)));
		Assert.Equal("cd", text.GetText(new TextSpan(4, 2)));
		Assert.Equal("ef", text.GetText(new TextSpan(7, 2)));
		Assert.Equal("", text.GetText(new TextSpan(9, 0)));
	}

	[Fact]
	public void GetText_Throws_WhenSpanExceedsSourceLength()
	{
		var text = SourceText.From("abc");

		Assert.Throws<ArgumentOutOfRangeException>(() => text.GetText(new TextSpan(1, 3)));
		Assert.Throws<ArgumentOutOfRangeException>(() => text.GetText(new TextSpan(4, 0)));
	}

	[Fact]
	public void Indexer_ReturnsUtf16Units()
	{
		var text = SourceText.From("abc");
		Assert.Equal('a', text[0]);
		Assert.Equal('c', text[2]);
	}

	[Fact]
	public void SurrogatePair_IsTwoUtf16Units_AndRoundTrips()
	{
		var text = SourceText.From("\U0001F600");

		Assert.Equal(2, text.Length);
		Assert.Equal((char)0xD83D, text[0]);
		Assert.Equal((char)0xDE00, text[1]);
		Assert.Equal(new LinePosition(0, 1), text.GetLinePosition(1));
		Assert.Equal(1, text.GetOffset(text.GetLinePosition(1)));
	}

	[Theory]
	[InlineData("\u00E9ll\u00F6 w\u00F6rld")]
	[InlineData("\u4E2D\u6587\u6587\u672C")]
	public void BmpCharacters_RoundTrip_AndIndexCorrectly(string source)
	{
		var text = SourceText.From(source);

		Assert.Equal(source, text.ToString());
		Assert.Equal(source, text.GetText(new TextSpan(0, source.Length)));

		for (var offset = 0; offset <= text.Length; offset++)
			Assert.Equal(offset, text.GetOffset(text.GetLinePosition(offset)));
	}

	[Fact]
	public void FinalNewline_ProducesAnEmptyTrailingLine()
	{
		var text = SourceText.From("abc\n");

		Assert.Equal(4, text.Length);
		Assert.Equal(new LinePosition(1, 0), text.GetLinePosition(4));
		Assert.Equal(4, text.GetOffset(text.GetLinePosition(4)));
	}

	[Fact]
	public void Tabs_AreCountedAsSingleCharacters()
	{
		var text = SourceText.From("a\tb");

		Assert.Equal(new LinePosition(0, 1), text.GetLinePosition(1));
		Assert.Equal(1, text.GetOffset(new LinePosition(0, 1)));
		Assert.Equal("\tb", text.GetText(new TextSpan(1, 2)));
	}

	[Fact]
	public void ToString_ReturnsTheOriginalText()
	{
		var source = "int main() {\n    return 0;\n}";
		Assert.Equal(source, SourceText.From(source).ToString());
	}
}
