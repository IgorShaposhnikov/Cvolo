using System;
using System.Collections.Generic;
using System.Linq;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// The authoritative keyword inventory, derived at runtime from the ANTLR-generated
/// <see cref="CvoloLexer"/> vocabulary. Completion relevance subsets are curated in
/// <see cref="CompletionKeywordProvider"/> because the lexer is context-free and the grammar
/// interleaves punctuation, but membership of every offered keyword is asserted against this
/// catalog by that provider's static constructor.
/// </summary>
/// <remarks>
/// Discovery does NOT rely on a magic numeric token range. ANTLR emits every keyword rule
/// (CvoloLexer.g4 lines 6-71) as a contiguous block of named token-type constants immediately
/// followed by punctuation (LPAREN=67 onward), so the keyword window is exactly
/// [<see cref="CvoloLexer.VAL"/>, <see cref="CvoloLexer.CATCH"/>] — the first and last keyword
/// rules in the grammar. Both bounds are the generated named constants, so adding, removing or
/// reordering a keyword renumbers those constants and this catalog tracks them without edits.
///
/// Vocabulary-only enumeration up to <c>IVocabulary.MaxTokenType</c> cannot isolate keywords:
/// punctuation tokens also carry literal names (LPAREN "'('", AND "'&&'", ARROW "'->'", ...),
/// so no clean metadata-only bound exists. The named-constant window is therefore the stable,
/// authoritative choice.
/// </remarks>
internal static class LexerKeywordCatalog
{
	private const int FirstKeywordTokenType = CvoloLexer.VAL;
	private const int LastKeywordTokenType = CvoloLexer.CATCH;

	private static readonly Lazy<IReadOnlySet<string>> All = new(() =>
		Enumerable.Range(FirstKeywordTokenType, LastKeywordTokenType - FirstKeywordTokenType + 1)
			.Select(tokenType => (CvoloLexer.DefaultVocabulary.GetLiteralName(tokenType) ?? "").Trim('\''))
			.Where(literal => literal.Length > 0)
			.ToHashSet(StringComparer.Ordinal));

	/// <summary>
	/// The full set of lexer keyword literals.
	/// </summary>
	public static IReadOnlySet<string> AllKeywords => All.Value;

	/// <summary>
	/// True when <paramref name="keyword"/> is a Cvolo lexer keyword literal.
	/// </summary>
	public static bool IsKeyword(string keyword)
	{
		return AllKeywords.Contains(keyword);
	}
}
