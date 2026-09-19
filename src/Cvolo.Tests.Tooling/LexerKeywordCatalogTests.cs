using Cvolo.Analysis.Completion;
using Cvolo.Compiler.Tooling.Internal;

namespace Cvolo.Tests.Tooling;

public sealed class LexerKeywordCatalogTests
{
	[Fact]
	public void Catalog_ContainsTheFullLexerKeywordRuleRange()
	{
		Assert.Equal(CvoloLexer.CATCH - CvoloLexer.VAL + 1, LexerKeywordCatalog.AllKeywords.Count);
		Assert.True(LexerKeywordCatalog.AllKeywords.SetEquals(
		[
			"val", "var", "refvar", "heap", "ref", "extern", "return", "if", "else", "while", "for",
			"true", "false", "null", "void", "int", "uint", "long", "ulong", "short", "ushort", "byte",
			"sbyte", "double", "float", "nint", "nuint", "bool", "string", "char", "struct", "union",
			"enum", "private", "internal", "public", "unsafe", "unbound", "panic", "extension",
			"interface", "protocol", "embed", "namespace", "using", "expose", "global", "switch",
			"case", "default", "where", "is", "alias", "defer", "asm", "volatile", "alignstack",
			"intel", "nameof", "typeof", "break", "continue", "foreach", "in", "try", "catch",
		]));
	}

	[Fact]
	public void Catalog_RecognizesExampleKeywords()
	{
		Assert.True(LexerKeywordCatalog.IsKeyword("val"));
		Assert.True(LexerKeywordCatalog.IsKeyword("return"));
		Assert.True(LexerKeywordCatalog.IsKeyword("int"));
		Assert.True(LexerKeywordCatalog.IsKeyword("nameof"));
		Assert.True(LexerKeywordCatalog.IsKeyword("catch"));
	}

	[Fact]
	public void Catalog_RejectsIdentifiersAndEmpty()
	{
		Assert.False(LexerKeywordCatalog.IsKeyword("foo"));
		Assert.False(LexerKeywordCatalog.IsKeyword("nintish"));
		Assert.False(LexerKeywordCatalog.IsKeyword(""));
	}

	[Fact]
	public void Catalog_BoundsAreDerivedFromGeneratedTokenConstants()
	{
		// Bounds are pinned to the generated named token constants (first/last keyword rule),
		// so '66' appears here only as the derived value CATCH - VAL + 1, never as a magic literal.
		var expectedWindow = Enumerable.Range(CvoloLexer.VAL, CvoloLexer.CATCH - CvoloLexer.VAL + 1)
			.Select(tokenType => CvoloLexer.DefaultVocabulary.GetLiteralName(tokenType)!.Trim('\''))
			.ToHashSet(StringComparer.Ordinal);

		Assert.True(LexerKeywordCatalog.AllKeywords.SetEquals(expectedWindow));
		Assert.Equal(CvoloLexer.CATCH - CvoloLexer.VAL + 1, LexerKeywordCatalog.AllKeywords.Count);

		// The first punctuation token (LPAREN, the rule immediately following the keyword block)
		// is NOT part of the catalog, proving the window stops exactly at the keyword boundary.
		var firstPunctuationLiteral = CvoloLexer.DefaultVocabulary.GetLiteralName(CvoloLexer.LPAREN)!.Trim('\'');
		Assert.False(LexerKeywordCatalog.IsKeyword(firstPunctuationLiteral));
	}

	[Fact]
	public void ProviderKeywordSubsets_AreAllLexerKeywords()
	{
		var staticMembers = CompletionKeywordProvider.TypeKeywordList;
		var contextMembers = CompletionKeywordProvider.For(CompletionQueryContext.Declaration)
			.Concat(CompletionKeywordProvider.For(CompletionQueryContext.Statement))
			.Concat(CompletionKeywordProvider.For(CompletionQueryContext.Expression));

		foreach (var keyword in staticMembers.Concat(contextMembers))
			Assert.True(LexerKeywordCatalog.IsKeyword(keyword), $"'{keyword}' is not a lexer keyword.");
	}
}
