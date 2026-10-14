using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// INC 3.1 G2/R1: parser and lexer diagnostics must land on exact original-source offsets and
/// must never be silently clamped or relocated. Impossible non-EOF coordinate mappings are
/// recorded through <see cref="AntlrSyntaxParser.RejectedImpossibleSpans"/> and omitted from
/// the outward diagnostic bag.
/// </summary>
public sealed class SyntaxSpanPrecisionTests : CompilerTestBase
{
	private static AntlrSyntaxParser Parse(string source)
	{
		var parser = new AntlrSyntaxParser();
		parser.Parse(new CompilationContext(source, "span.cvl"));
		return parser;
	}

	private static void AssertAllSpansInBounds(AntlrSyntaxParser parser, string source)
	{
		foreach (var diagnostic in parser.Diagnostics.Diagnostics)
		{
			Assert.InRange(diagnostic.Span.Start, 0, source.Length);
			Assert.InRange(diagnostic.Span.End, 0, source.Length);
			Assert.True(diagnostic.Span.Start <= diagnostic.Span.End);
		}
	}

	[Fact]
	public void ValidProgram_ReportsNoRejectedImpossibleSpans()
	{
		const string source = "int main() { val x = 1 + 2; return 0; }\n";
		var parser = Parse(source);
		Assert.Empty(parser.RejectedImpossibleSpans);
		AssertAllSpansInBounds(parser, source);
	}

	[Fact]
	public void MalformedProgram_ReportsNoRejectedImpossibleSpans()
	{
		const string source = "int main( {\n    return 0;\n}\n";
		var parser = Parse(source);
		Assert.Empty(parser.RejectedImpossibleSpans);
		AssertAllSpansInBounds(parser, source);
	}

	[Fact]
	public void CeofError_IsZeroWidthAtEndOfSource_NotRelocated()
	{
		const string source = "int main() {\n    val x = ";
		var parser = Parse(source);
		Assert.Empty(parser.RejectedImpossibleSpans);

		// The unterminated expression surfaces as an EOF-anchored diagnostic whose span is a
		// zero-width insertion point at the real end of the buffer.
		var eof = Assert.Single(parser.Diagnostics.Diagnostics, d => d.Id == "CVL4175");
		Assert.Equal(source.Length, eof.Span.Start);
		Assert.Equal(0, eof.Span.Length);
	}

	[Fact]
	public void CrlfSource_KeepsOffsetsAlignedToOriginalText()
	{
		const string source = "int main()\r\n{\r\n    val x = 1;\r\n    return 0;\r\n}\r\n";
		var parser = Parse(source);
		Assert.Empty(parser.RejectedImpossibleSpans);
		Assert.Empty(parser.Diagnostics.Diagnostics);
	}

	[Fact]
	public void SupplementaryUnicode_DoesNotDriftOffsets()
	{
		// '😀' is a single scalar value that counts as two UTF-16 code units; a fault after it must
		// still be reported at its real UTF-16 offset (no drift from the surrogate pair).
		const string source = "int main() { val s = \"😀\"; val x = 1value; return 0; }\n";
		var parser = Parse(source);
		Assert.Empty(parser.RejectedImpossibleSpans);
		AssertAllSpansInBounds(parser, source);

		var badStart = source.IndexOf("1value", System.StringComparison.Ordinal);
		Assert.Contains(parser.Diagnostics.Diagnostics, d => d.Id == "CVL1903" && d.Span.Start == badStart);
	}

	[Fact]
	public void UnrecognizedCharacter_IsReportedOnTheExactSourceOffset()
	{
		const string source = "int main() { val x = 0; $; return 0; }\n";
		var parser = Parse(source);
		Assert.Empty(parser.RejectedImpossibleSpans);

		var dollar = source.IndexOf('$');
		var finding = Assert.Single(parser.Diagnostics.Diagnostics, d => d.Id == "CVL1911");
		Assert.Equal(dollar, finding.Span.Start);
		Assert.Equal(1, finding.Span.Length);
	}
}
