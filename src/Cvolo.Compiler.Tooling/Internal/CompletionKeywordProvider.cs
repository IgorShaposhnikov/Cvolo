using Cvolo.Analysis.Completion;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Compiler-owned keyword surface for completion contexts. The exact keyword spellings are supplied
/// by the authoritative lexer: every word below is asserted to be a member of
/// <see cref="LexerKeywordCatalog.AllKeywords"/> (derived from the ANTLR vocabulary), in a static
/// constructor. The context relevance subsets are curated here because the lexer is context-free and
/// the grammar interleaves punctuation; each set mirrors the grammar positions where a keyword may
/// begin a construct. The service applies prefix filtering and type-context narrowing.
/// </summary>
internal static class CompletionKeywordProvider
{
	static CompletionKeywordProvider()
	{
		foreach (var keyword in TypeKeywords
			.Concat(ExpressionKeywords)
			.Concat(DeclarationKeywords)
			.Concat(StatementKeywords)
			.Distinct())
		{
			if (!LexerKeywordCatalog.IsKeyword(keyword))
				throw new InvalidOperationException($"CompletionKeywordProvider references '{keyword}', which is not a Cvolo lexer keyword.");
		}
	}

	private static readonly string[] TypeKeywords =
	[
		"int", "uint", "long", "ulong", "short", "ushort",
		"byte", "sbyte", "nint", "nuint", "float", "double",
		"bool", "string", "char", "void", "ref", "refvar"
	];

	private static readonly string[] ExpressionKeywords =
	[
		"asm", "nameof", "typeof", "heap", "true", "false",
		"null", "void", "default", "ref", "refvar", "panic"
	];

	private static readonly string[] DeclarationKeywords =
	[
		"alias", "global", "val", "var", "private", "internal", "public",
		"extern", "expose", "struct", "union", "enum", "extension",
		"interface", "protocol", "unsafe", "unbound", "namespace", "using",
		.. TypeKeywords
	];

	private static readonly string[] StatementKeywords =
	[
		"return", "val", "var", "ref", "refvar", "if", "while",
		"for", "foreach", "switch", "defer", "try", "break",
		"continue", "unsafe",
		.. ExpressionKeywords
	];

	/// <summary>
	/// Returns the keyword literals offered in the given completion context, in deterministic order.
	/// Member and None contexts offer no keywords.
	/// </summary>
	public static IReadOnlyList<string> For(CompletionQueryContext context) => context switch
	{
		CompletionQueryContext.Declaration => DeclarationKeywords,
		CompletionQueryContext.Statement => StatementKeywords,
		CompletionQueryContext.Expression => ExpressionKeywords,
		_ => []
	};

	/// <summary>
	/// Returns the type keywords retained when the token context indicates a type-only position.
	/// </summary>
	public static IReadOnlyList<string> TypeKeywordList => TypeKeywords;

	/// <summary>
	/// True when <paramref name="label"/> is a type keyword.
	/// </summary>
	public static bool IsTypeKeyword(string label)
	{
		foreach (var keyword in TypeKeywords)
		{
			if (keyword == label)
				return true;
		}

		return false;
	}
}
