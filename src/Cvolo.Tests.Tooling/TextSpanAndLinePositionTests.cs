using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class TextSpanAndLinePositionTests
{
	[Theory]
	[InlineData(-1, 0)]
	[InlineData(0, -1)]
	[InlineData(int.MaxValue, 1)]
	[InlineData(3, int.MaxValue)]
	[InlineData(int.MaxValue, int.MaxValue)]
	public void TextSpan_InvalidValues_ThrowArgumentOutOfRangeException(int start, int length)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(start, length));
	}

	[Fact]
	public void TextSpan_ValidValues_ExposeStartLengthEnd()
	{
		var span = new TextSpan(5, 3);

		Assert.Equal(5, span.Start);
		Assert.Equal(3, span.Length);
		Assert.Equal(8, span.End);
	}

	[Fact]
	public void TextSpan_ZeroLength_IsValidAtAnyPosition()
	{
		Assert.Equal(0, new TextSpan(0, 0).Length);
		Assert.Equal(0, new TextSpan(10, 0).Length);
	}

	[Fact]
	public void TextSpan_Equality_IsStructural()
	{
		Assert.Equal(new TextSpan(2, 4), new TextSpan(2, 4));
		Assert.NotEqual(new TextSpan(2, 4), new TextSpan(2, 5));
		Assert.True(new TextSpan(1, 1) == new TextSpan(1, 1));
	}

	[Theory]
	[InlineData(-1, 0)]
	[InlineData(1, -1)]
	public void LinePosition_InvalidValues_ThrowArgumentOutOfRangeException(int line, int character)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new LinePosition(line, character));
	}

	[Fact]
	public void LinePosition_ValidValues_ExposeLineAndCharacter()
	{
		var position = new LinePosition(3, 7);

		Assert.Equal(3, position.Line);
		Assert.Equal(7, position.Character);
	}

	[Fact]
	public void LinePosition_Origin_IsValid()
	{
		Assert.Equal(new LinePosition(0, 0), new LinePosition(0, 0));
	}

	[Fact]
	public void LinePosition_Equality_IsStructural()
	{
		Assert.Equal(new LinePosition(1, 2), new LinePosition(1, 2));
		Assert.NotEqual(new LinePosition(1, 2), new LinePosition(2, 1));
	}
}
