using Antlr4.Runtime;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Lexical context of a completion request: whether the cursor is inside a comment or string/char
/// literal (which suppresses candidates), the replacement span covering the identifier or keyword
/// being typed (or a zero-length span), the typed prefix, whether the previous significant token
/// indicates a type-only position, and whether the cursor is lexically in a member-access slot.
/// Offsets and spans are UTF-16 based, matching <see cref="SourceText"/> and the lexer.
/// </summary>
internal sealed record CompletionTextContext(
	bool IsCommentOrLiteral,
	TextSpan ReplacementSpan,
	string Prefix,
	bool IsTypeContext,
	bool IsMemberAccessContext,
	bool IsDestructorContext,
	string DestructorTypeName,
	bool IsAttributeContext = false)
{
	/// <summary>
	/// True when the cursor sits in the value region of an aggregate literal member (after the
	/// <c>=</c> or <c>:</c> that follows a field name), as opposed to the field-name region.
	/// </summary>
	internal bool IsAggregateInitializerValue { get; init; }

	/// <summary>
	/// True when the cursor sits in the name slot of a declaration: after a resolved type name
	/// (for example <c>Window |</c>) or while naming it (<c>Window g|</c>), as opposed to an
	/// expression or statement position. The name slot must not fall back to the ambient value
	/// surface, which would leak unrelated functions and globals.
	/// </summary>
	internal bool IsDeclarationNameContext { get; init; }

	/// <summary>
	/// True when the cursor sits in the field-name slot of an aggregate literal, including an
	/// incomplete one where the parser lost the initializer node (for example
	/// <c>Player { Body = 1, | }</c> or <c>Player { d| }</c>). Such a slot must offer the
	/// aggregate's own fields and never fall back to globals, functions, or keywords.
	/// </summary>
	internal bool IsAggregateInitializerName { get; init; }

	/// <summary>
	/// True when the cursor sits inside an open generic type-argument list
	/// (<c>Option&lt;i|</c>, <c>Option&lt;Option&lt;str|</c>), where only type names are legal.
	/// Statement keywords such as <c>if</c> must not be offered there.
	/// </summary>
	internal bool IsTypeArgumentContext { get; init; }

	/// <summary>
	/// Cursor is at the start of a statement in a function/control block.
	/// Used only when a malformed document cannot produce a semantic query,
	/// so keyword completion survives without leaking into struct fields or
	/// aggregate initializer member names.
	/// </summary>
	internal bool IsStatementStartContext { get; init; }

	/// <summary>
	/// The field name being assigned when the cursor sits in an aggregate initializer value slot
	/// (<c>Window { Ptr = | }</c>), when it can be read from the surrounding tokens.
	/// </summary>
	internal string? AggregateInitializerFieldName { get; init; }

	/// <summary>
	/// The aggregate type name of the enclosing initializer value slot, when it can be read from
	/// the surrounding tokens.
	/// </summary>
	internal string? AggregateInitializerTypeName { get; init; }

	internal static CompletionTextContext Analyze(SourceText text, int position)
	{
		var context = AnalyzeCore(text, position);
		if (context.IsTypeContext || context.IsCommentOrLiteral
			|| context.IsMemberAccessContext || context.IsDestructorContext)
		{
			return context;
		}

		var tokens = Lex(text.ToString());
		if (DetectAggregateInitializerValue(tokens, position, out var valueField, out var valueType))
		{
			context = context with
			{
				IsAggregateInitializerValue = true,
				AggregateInitializerFieldName = valueField,
				AggregateInitializerTypeName = valueType,
			};
		}
		if (DetectAggregateInitializerName(tokens, position))
			context = context with { IsAggregateInitializerName = true };
		if (DetectDeclarationNameContext(tokens, position))
			context = context with { IsDeclarationNameContext = true };
		if (DetectTypeArgumentContext(tokens, position))
			context = context with { IsTypeArgumentContext = true };
		if (!context.IsTypeArgumentContext && DetectDeclarationTypeContext(tokens, position))
			context = context with { IsTypeContext = true };
		if (!context.IsTypeContext && !context.IsAggregateInitializerName
			&& !context.IsAggregateInitializerValue
			&& DetectStatementStartContext(tokens, position))
			context = context with { IsStatementStartContext = true };
		return context;
	}

	/// <summary>
	/// The cursor is in a declaration-only type position — a parameter type list or an aggregate
	/// body's field type list — where statement keywords such as <c>if</c> are not legal. This is
	/// narrower than a value position, where both types and keywords may appear.
	/// </summary>
	private static bool DetectDeclarationTypeContext(IList<IToken> tokens, int position)
	{
		var last = LastSignificantIndexBefore(tokens, position);
		if (last < 0)
			return false;
		if (IsWordToken(tokens[last].Type) || tokens[last].Text == ">")
			last = PreviousSignificantIndex(tokens, last);
		if (last < 0)
			return false;

		var token = tokens[last];
		if (token.Type == CvoloLexer.LPAREN || token.Type == CvoloLexer.COMMA)
			return OpensDeclarationParameterList(tokens, last);
		if (token.Type == CvoloLexer.LBRACE)
			return BraceOpensAggregateDeclaration(tokens, last);
		if (token.Type == CvoloLexer.SEMI)
		{
			// A field type following an earlier field ends up after ';', not '{'.
			// Locate the innermost enclosing brace rather than inspecting only the
			// immediate predecessor. This must not classify a function statement or
			// aggregate initializer as a struct/union field declaration.
			var depth = 0;
			for (var i = last - 1; i >= 0; i--)
			{
				var candidate = tokens[i];
				if (candidate.Channel != Lexer.DefaultTokenChannel || candidate.Type == TokenConstants.EOF)
					continue;
				if (candidate.Type == CvoloLexer.RBRACE)
				{
					depth++;
					continue;
				}
				if (candidate.Type != CvoloLexer.LBRACE)
					continue;
				if (depth == 0)
					return BraceOpensAggregateDeclaration(tokens, i);
				depth--;
			}
		}
		return false;
	}

	/// <summary>
	/// Recognize a statement boundary even when parser recovery lost the
	/// containing function. Use the innermost unmatched brace to distinguish
	/// a function/control block from a struct, namespace, or initializer.
	/// </summary>
	private static bool DetectStatementStartContext(IList<IToken> tokens, int position)
	{
		var previous = LastSignificantIndexBefore(tokens, position);
		if (previous < 0)
			return false;
		if (IsWordToken(tokens[previous].Type))
			previous = PreviousSignificantIndex(tokens, previous);
		if (previous < 0 || tokens[previous].Type is not
			(CvoloLexer.LBRACE or CvoloLexer.SEMI or CvoloLexer.RBRACE))
			return false;

		var depth = 0;
		for (var i = previous; i >= 0; i--)
		{
			var token = tokens[i];
			if (token.Channel != Lexer.DefaultTokenChannel || token.Type == TokenConstants.EOF)
				continue;
			if (token.Type == CvoloLexer.RBRACE)
			{
				depth++;
				continue;
			}
			if (token.Type != CvoloLexer.LBRACE)
				continue;
			if (depth > 0)
			{
				depth--;
				continue;
			}
			if (BraceOpensAggregateDeclaration(tokens, i))
				return false;
			var beforeBrace = PreviousSignificantIndex(tokens, i);
			return beforeBrace >= 0 && tokens[beforeBrace].Type is
				(CvoloLexer.RPAREN or CvoloLexer.ELSE or CvoloLexer.TRY or CvoloLexer.FINALLY);
		}
		return false;
	}

	/// <summary>
	/// A parameter type slot is declaration-only when the enclosing <c>(</c> is written directly
	/// after a declared function name, which itself follows a type (so <c>int f(d|)</c> is a
	/// declaration, while <c>x = f(d|)</c> and <c>foo(bar, d|)</c> are calls).
	/// </summary>
	private static bool OpensDeclarationParameterList(IList<IToken> tokens, int separatorIndex)
	{
		var open = separatorIndex;
		if (tokens[separatorIndex].Type == CvoloLexer.COMMA)
		{
			open = -1;
			var depth = 0;
			for (var i = separatorIndex; i >= 0; i--)
			{
				var t = tokens[i];
				if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF || t.Type == CvoloLexer.WS)
					continue;
				if (t.Type == CvoloLexer.RPAREN)
				{
					depth++;
				}
				else if (t.Type == CvoloLexer.LPAREN)
				{
					if (depth == 0)
					{
						open = i;
						break;
					}

					depth--;
				}
			}

			if (open < 0)
				return false;
		}

		var nameIndex = PreviousSignificantIndex(tokens, open);
		if (nameIndex < 0 || tokens[nameIndex].Type != CvoloLexer.Identifier)
			return false;
		var beforeName = PreviousSignificantIndex(tokens, nameIndex);
		return beforeName >= 0 && IsTypeLikeToken(tokens[beforeName]);
	}

	/// <summary>
	/// A field type slot is declaration-only when the innermost unclosed <c>{</c> directly
	/// follows a <c>struct</c>/<c>union</c> name (so <c>struct S { d| }</c> is a declaration,
	/// while <c>Window { d| }</c> and <c>if (ok) { d| }</c> are not).
	/// </summary>
	private static bool BraceOpensAggregateDeclaration(IList<IToken> tokens, int braceIndex)
	{
		var nameIndex = PreviousSignificantIndex(tokens, braceIndex);
		if (nameIndex < 0 || tokens[nameIndex].Type != CvoloLexer.Identifier)
			return false;
		var keywordIndex = PreviousSignificantIndex(tokens, nameIndex);
		return keywordIndex >= 0 && tokens[keywordIndex].Type is CvoloLexer.STRUCT or CvoloLexer.UNION;
	}

	private static int LastSignificantIndexBefore(IList<IToken> tokens, int position)
	{
		for (var i = tokens.Count - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF || t.Type == CvoloLexer.WS)
				continue;
			if (t.StopIndex >= position)
				continue;
			return i;
		}

		return -1;
	}

	/// <summary>
	/// The cursor is inside an open generic type-argument list when an unmatched <c>&lt;</c> opens
	/// before the cursor (nested openers are tracked through <c>&gt;</c> depth) and no statement
	/// boundary intervenes. Used to narrow a type-argument slot to type names only.
	/// </summary>
	private static bool DetectTypeArgumentContext(IList<IToken> tokens, int position)
	{
		var depth = 0;
		for (var i = tokens.Count - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF || t.Type == CvoloLexer.WS)
				continue;
			if (t.StopIndex >= position)
				continue;

			var text = t.Text;
			if (text.Length > 0 && text[^1] == '>')
			{
				depth++;
				continue;
			}

			if (text == "<")
			{
				if (depth > 0)
				{
					depth--;
					continue;
				}

				return true;
			}

			if (depth == 0 && t.Type is CvoloLexer.SEMI or CvoloLexer.LBRACE or CvoloLexer.RBRACE)
				return false;
		}

		return false;
	}

	/// <summary>
	/// The cursor is in a field-value slot when the last significant token before it is the
	/// <c>=</c> or <c>:</c> written after a field name inside a braced initializer. Also reads the
	/// field name and the enclosing aggregate type name from the surrounding tokens so the
	/// expected value type can be judged even when the parser dropped the initializer node.
	/// </summary>
	private static bool DetectAggregateInitializerValue(
		IList<IToken> tokens, int position, out string? fieldName, out string? typeName)
	{
		fieldName = null;
		typeName = null;

		var assignIndex = LastSignificantIndexBefore(tokens, position);
		if (assignIndex < 0 || tokens[assignIndex].Type is not (CvoloLexer.ASSIGN or CvoloLexer.COLON))
			return false;

		var nameIndex = PreviousSignificantIndex(tokens, assignIndex);
		if (nameIndex < 0)
			return true;

		if (tokens[nameIndex].Type == CvoloLexer.Identifier)
			fieldName = tokens[nameIndex].Text;

		var braceIndex = PreviousSignificantIndex(tokens, nameIndex);
		while (braceIndex >= 0 && tokens[braceIndex].Type == CvoloLexer.COMMA)
			braceIndex = PreviousSignificantIndex(tokens, braceIndex);

		if (braceIndex >= 0 && tokens[braceIndex].Type == CvoloLexer.LBRACE)
		{
			var typeIndex = PreviousSignificantIndex(tokens, braceIndex);
			if (typeIndex >= 0 && tokens[typeIndex].Type == CvoloLexer.Identifier)
				typeName = tokens[typeIndex].Text;
		}

		return true;
	}

	/// <summary>
	/// The cursor is in the field-name slot of an aggregate literal when the last significant
	/// token before it is <c>{</c>, <c>,</c>, or an identifier, and the innermost unmatched
	/// <c>{</c> is opened by an aggregate type name (an identifier, a nullable <c>?</c>, or a
	/// generic name ending in <c>&gt;</c>). Combined with the AST initializer check, this also
	/// recognises incomplete input whose initializer node the parser dropped.
	/// </summary>
	private static bool DetectAggregateInitializerName(IList<IToken> tokens, int position)
	{
		var lastIndex = -1;
		for (var i = 0; i < tokens.Count; i++)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF || t.Type == CvoloLexer.WS)
				continue;
			if (t.StopIndex >= position)
				continue;
			lastIndex = i;
		}

		if (lastIndex < 0)
			return false;

		var lastType = tokens[lastIndex].Type;
		if (lastType != CvoloLexer.LBRACE && lastType != CvoloLexer.COMMA && lastType != CvoloLexer.Identifier)
			return false;

		var depth = 0;
		for (var i = lastIndex; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF || t.Type == CvoloLexer.WS)
				continue;

			if (t.Type == CvoloLexer.RBRACE)
			{
				depth++;
				continue;
			}

			if (t.Type != CvoloLexer.LBRACE)
				continue;

			if (depth > 0)
			{
				depth--;
				continue;
			}

			var prevIndex = PreviousSignificantIndex(tokens, i);
			if (prevIndex < 0)
				return false;

			var prev = tokens[prevIndex];
			return prev.Type == CvoloLexer.Identifier
				|| prev.Type == CvoloLexer.QMARK
				|| (prev.Text.Length > 0 && prev.Text[^1] == '>');
		}

		return false;
	}

	private static int PreviousSignificantIndex(IList<IToken> tokens, int index)
	{
		for (var i = index - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF || t.Type == CvoloLexer.WS)
				continue;
			return i;
		}

		return -1;
	}

	/// <summary>
	/// Probe texts that complete an incomplete aggregate initializer so it parses: a placeholder
	/// member is inserted at the cursor (everything before the cursor is untouched, so the
	/// replacement span is preserved), optionally followed by a closing brace when the current
	/// line leaves one open.
	/// </summary>
	internal static IEnumerable<string> GetInitializerProbeTexts(SourceText text, int position)
	{
		var source = text.ToString();
		var insert = "__cvolo_probe__ = 0";
		yield return source[..position] + insert + source[position..];

		var rest = source[position..];
		var lineEnd = rest.IndexOf('\n');
		var line = lineEnd < 0 ? rest : rest[..lineEnd];
		if (!line.Contains('}'))
			yield return source[..position] + insert + " }" + source[position..];
	}

	/// <summary>
	/// The cursor is in a declaration name slot when the trailing word is a type name written
	/// before an empty name slot (<c>Window |</c>) or a partial name following a type or a
	/// declaration prefix (<c>Window g|</c>, <c>val g|</c>, <c>val int count|</c>). A trailing
	/// word alone (<c>Win|</c>), or a word following a non-type keyword such as <c>return</c>,
	/// stays out of the declaration-name context.
	/// </summary>
	private static bool DetectDeclarationNameContext(IList<IToken> tokens, int position)
	{
		var significant = new List<IToken>();
		for (var i = 0; i < tokens.Count; i++)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF || t.Type == CvoloLexer.WS)
				continue;
			if (t.StopIndex >= position)
				continue;

			significant.Add(t);
		}

		if (significant.Count == 0)
			return false;

		var last = significant[^1];
		if (last.Type != CvoloLexer.Identifier)
			return false;

		// `Window |`: the type name is complete (there is whitespace up to the cursor) and the
		// name slot is empty. `Window g|`: the trailing identifier is a partial name adjoining a
		// preceding type or declaration prefix.
		var hasGap = position - (last.StopIndex + 1) > 0;
		var prev = significant.Count >= 2 ? significant[^2] : null;

		if (hasGap)
		{
			return prev is null
				|| IsStatementBoundary(prev.Type)
				|| IsDeclarationPrefixType(prev.Type)
				|| IsVisibilityType(prev.Type);
		}

		if (prev is null || (!IsTypeLikeToken(prev) && !IsDeclarationPrefixType(prev.Type)))
			return false;

		var beforePrev = significant.Count >= 3 ? significant[^3] : null;
		return beforePrev is null
			|| IsStatementBoundary(beforePrev.Type)
			|| IsDeclarationPrefixType(beforePrev.Type)
			|| IsVisibilityType(beforePrev.Type);
	}

	private static bool IsTypeLikeToken(IToken token)
		=> token.Type == CvoloLexer.Identifier || PrimitiveTypeKeywords.Contains(token.Text);

	private static bool IsDeclarationPrefixType(int tokenType) => tokenType switch
	{
		CvoloLexer.VAL or CvoloLexer.VAR or CvoloLexer.GLOBAL or CvoloLexer.ALIAS
			or CvoloLexer.REF or CvoloLexer.REFVAR => true,
		_ => false
	};

	private static bool IsVisibilityType(int tokenType) => tokenType switch
	{
		CvoloLexer.PRIVATE or CvoloLexer.INTERNAL or CvoloLexer.PUBLIC => true,
		_ => false
	};

	private static bool IsStatementBoundary(int tokenType) => tokenType switch
	{
		CvoloLexer.SEMI or CvoloLexer.LBRACE or CvoloLexer.RBRACE => true,
		_ => false
	};

	private static readonly HashSet<string> PrimitiveTypeKeywords = new(StringComparer.Ordinal)
	{
		"void", "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
		"double", "float", "nint", "nuint", "bool", "string", "char"
	};

	private static CompletionTextContext AnalyzeCore(SourceText text, int position)
	{
		var source = text.ToString();
		var tokens = Lex(source);
		var isAttributeContext = DetectAttributeContext(source, tokens, position);

		var (token, tokenIndex) = FindCoveringToken(tokens, position);

		if (token is not null)
		{
			if (IsCommentOrLiteralType(token.Type))
			{
				var start = BackscanWordStart(source, position - 1, token.StartIndex);
				return new CompletionTextContext(true, new TextSpan(start, position - start),
					source.Substring(start, position - start), false, false, false, string.Empty);
			}

			if (IsWordToken(token.Type))
			{
				if (TryBuildDestructor(tokens, FindPreviousTildeIndex(tokens, tokenIndex), position, out var destructorSpan, out var destructorName))
				{
					return new CompletionTextContext(false, destructorSpan,
						source.Substring(destructorSpan.Start, position - destructorSpan.Start),
						false, false, true, destructorName);
				}

				var prefix = source.Substring(token.StartIndex, position - token.StartIndex);
				var tokenLength = token.StopIndex - token.StartIndex + 1;
				return new CompletionTextContext(false, new TextSpan(token.StartIndex, tokenLength),
					prefix, PreviousSignificantTokenIsTypeMarker(tokens, tokenIndex),
					PreviousSignificantTokenIsDot(tokens, tokenIndex), false, string.Empty, isAttributeContext);
			}
		}
		else
		{
			// Cursor positions are token boundaries. Prefer an adjacent preceding word token
			// (e.g. `p.c|;`) over the token that starts at the cursor; otherwise a word
			// beginning exactly at the cursor (e.g. `val |Point p`) is the replacement target.
			var (prev, prevIndex) = FindPreviousToken(tokens, position);
			if (prev is not null && IsWordToken(prev.Type))
			{
				if (TryBuildDestructor(tokens, FindPreviousTildeIndex(tokens, prevIndex), position, out var destructorSpan, out var destructorName))
				{
					return new CompletionTextContext(false, destructorSpan,
						source.Substring(destructorSpan.Start, position - destructorSpan.Start),
						false, false, true, destructorName);
				}

				var prefix = source.Substring(prev.StartIndex, position - prev.StartIndex);
				var tokenLength = prev.StopIndex - prev.StartIndex + 1;
				return new CompletionTextContext(false, new TextSpan(prev.StartIndex, tokenLength),
					prefix, PreviousSignificantTokenIsTypeMarker(tokens, prevIndex),
					PreviousSignificantTokenIsDot(tokens, prevIndex), false, string.Empty, isAttributeContext);
			}

			var (nextWord, nextWordIndex) = FindWordTokenStartingAt(tokens, position);
			if (nextWord is not null)
			{
				var tokenLength = nextWord.StopIndex - nextWord.StartIndex + 1;
				return new CompletionTextContext(false, new TextSpan(nextWord.StartIndex, tokenLength),
					string.Empty, PreviousSignificantTokenIsTypeMarker(tokens, nextWordIndex),
					PreviousSignificantTokenIsDot(tokens, nextWordIndex), false, string.Empty, isAttributeContext);
			}
		}

		// Cursor immediately after a bare `~` (the destructor marker in an extension body).
		if (TryBuildDestructor(tokens, FindLastTildeIndex(tokens, position), position, out var bareSpan, out var bareName))
		{
			return new CompletionTextContext(false, bareSpan, string.Empty, false, false, true, bareName);
		}

		return new CompletionTextContext(false, new TextSpan(position, 0), string.Empty,
			LastSignificantTokenIsTypeMarker(tokens, position),
			LastSignificantTokenIsDot(tokens, position), false, string.Empty, isAttributeContext);
	}

	/// <summary>
	/// True when the cursor sits inside an attribute list (<c>[...]</c>) that opens a declaration,
	/// rather than an array index. An attribute list's <c>[</c> is either the first token on its
	/// line or follows a declaration terminator (<c>;</c>, <c>{</c>, <c>}</c>); an array index's
	/// <c>[</c> follows an expression.
	/// </summary>
	private static bool DetectAttributeContext(string source, IList<IToken> tokens, int position)
	{
		var bracketDepth = 0;
		var parenDepth = 0;
		for (var i = tokens.Count - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF)
				continue;
			if (t.StopIndex >= position)
				continue;

			switch (t.Type)
			{
				case CvoloLexer.RPAREN:
					parenDepth++;
					continue;
				case CvoloLexer.LPAREN:
					if (parenDepth > 0)
					{
						parenDepth--;
						continue;
					}

					// An unclosed '(' before any '[' means the cursor is inside arguments,
					// not the attribute name itself.
					return false;
				case CvoloLexer.RBRACK:
					bracketDepth++;
					continue;
				case CvoloLexer.LBRACK:
					if (bracketDepth > 0)
					{
						bracketDepth--;
						continue;
					}

					return parenDepth == 0 && OpensDeclaration(source, tokens, i);
				default:
					continue;
			}
		}

		return false;
	}

	private static bool OpensDeclaration(string source, IList<IToken> tokens, int bracketIndex)
	{
		var bracket = tokens[bracketIndex];

		// Only whitespace between the start of the line and the '['?
		var i = bracket.StartIndex - 1;
		while (i >= 0 && (source[i] == ' ' || source[i] == '\t'))
			i--;
		if (i < 0 || source[i] == '\n' || source[i] == '\r')
			return true;

		for (var j = bracketIndex - 1; j >= 0; j--)
		{
			var t = tokens[j];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF || t.Type == CvoloLexer.WS)
				continue;

			return t.Type is CvoloLexer.SEMI or CvoloLexer.LBRACE or CvoloLexer.RBRACE;
		}

		return true;
	}

	/// <summary>
	/// Builds the destructor replacement span (from the <c>~</c> through the cursor) and resolves the
	/// extended type name, when <paramref name="tildeIndex"/> is a real destructor marker inside an
	/// extension body. Returns false for a bitwise-not <c>~</c> (no enclosing extension).
	/// </summary>
	private static bool TryBuildDestructor(IList<IToken> tokens, int tildeIndex, int position, out TextSpan span, out string typeName)
	{
		if (tildeIndex < 0 || !TryGetEnclosingExtensionName(tokens, tildeIndex, out typeName))
		{
			span = default;
			typeName = string.Empty;
			return false;
		}

		var tilde = tokens[tildeIndex];
		span = new TextSpan(tilde.StartIndex, position - tilde.StartIndex);
		return true;
	}

	private static int FindPreviousTildeIndex(IList<IToken> tokens, int beforeIndex)
	{
		for (var i = beforeIndex - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF)
				continue;
			if (t.Type == CvoloLexer.WS)
				continue;

			return t.Type == CvoloLexer.TILDE ? i : -1;
		}

		return -1;
	}

	private static int FindLastTildeIndex(IList<IToken> tokens, int position)
	{
		for (var i = tokens.Count - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF)
				continue;
			if (t.StopIndex >= position)
				continue;

			return t.Type == CvoloLexer.TILDE ? i : -1;
		}

		return -1;
	}

	/// <summary>
	/// Resolves the name of the extension whose body encloses <paramref name="tildeIndex"/>. Scans back
	/// to the matching <c>{</c> (tracking nested braces) and then to the nearest preceding
	/// <c>extension</c> keyword, whose following identifier is the extended type name.
	/// </summary>
	private static bool TryGetEnclosingExtensionName(IList<IToken> tokens, int tildeIndex, out string name)
	{
		var brace = -1;
		var depth = 0;
		for (var i = tildeIndex - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Type == CvoloLexer.RBRACE)
			{
				depth++;
			}
			else if (t.Type == CvoloLexer.LBRACE)
			{
				if (depth == 0)
				{
					brace = i;
					break;
				}

				depth--;
			}
		}

		if (brace < 0)
		{
			name = string.Empty;
			return false;
		}

		for (var i = brace - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Type == CvoloLexer.SEMI || t.Type == CvoloLexer.RBRACE || t.Type == CvoloLexer.LBRACE)
				break;
			if (t.Type != CvoloLexer.EXTENSION)
				continue;

			for (var j = i + 1; j < tokens.Count; j++)
			{
				var next = tokens[j];
				if (next.Channel != Lexer.DefaultTokenChannel || next.Type == TokenConstants.EOF)
					continue;
				if (next.Type == CvoloLexer.WS)
					continue;
				if (next.Type == CvoloLexer.Identifier)
				{
					name = next.Text;
					return true;
				}

				break;
			}
		}

		name = string.Empty;
		return false;
	}

	internal const string MemberProbeIdentifier = "__cvoloCompletionProbe";

	internal static bool TryGetProbeText(SourceText text, int position, out string probeText)
	{
		if (position < 0 || position > text.Length)
		{
			probeText = string.Empty;
			return false;
		}

		var source = text.ToString();
		var tokens = Lex(source);

		if (LastSignificantTokenIsDot(tokens, position))
		{
			var insertion = MemberProbeIdentifier;
			if (NeedsStatementTerminator(source, position))
				insertion += ";";

			probeText = source.Insert(position, insertion);
			return true;
		}

		if (position > 0 && IsWordChar(source[position - 1]))
		{
			probeText = source.Insert(position, ";");
			return true;
		}

		if (LastSignificantTokenIsTypeMarker(tokens, position))
		{
			probeText = source.Insert(position, MemberProbeIdentifier);
			return true;
		}

		probeText = string.Empty;
		return false;
	}

	/// <summary>
	/// Probe (text, position) candidates for a document whose primary parse failed, tried in order by
	/// the completion recovery path. The first is the standard probe from <see cref="TryGetProbeText"/>;
	/// the second removes a half-typed word so a top-level partial keyword (which otherwise makes the
	/// whole file unparseable) leaves a parseable file whose context at the word start still describes
	/// the position being typed.
	/// </summary>
	internal static IReadOnlyList<(string Text, int Position)> GetProbeTexts(SourceText text, int position)
	{
		var source = text.ToString();
		var probes = new List<(string Text, int Position)>();

		if (TryGetProbeText(text, position, out var primary))
			probes.Add((primary, position));

		var wordStart = position;
		while (wordStart > 0 && IsWordChar(source[wordStart - 1]))
			wordStart--;

		if (wordStart < position)
			probes.Add((source.Remove(wordStart, position - wordStart), wordStart));

		return probes;
	}

	/// <summary>
	/// Probe texts for a trailing member-access dot, tried in order by the completion recovery path:
	/// the bare probe identifier (a nested expression such as <c>f(receiver.)</c>) and the probe with a
	/// statement terminator (a standalone statement such as <c>receiver.</c> before a following
	/// statement, <c>}</c>, or end of file). The correct form depends on the surrounding parse, so the
	/// caller tries each and keeps the first that yields a member context.
	/// </summary>
	internal static IReadOnlyList<string> GetMemberProbeTexts(SourceText text, int position)
	{
		var source = text.ToString();
		return
		[
			source.Insert(position, MemberProbeIdentifier),
			source.Insert(position, MemberProbeIdentifier + ";"),
		];
	}

	private static bool IsWordChar(char ch)
	{
		return char.IsLetterOrDigit(ch) || ch == '_';
	}

	private static IList<IToken> Lex(string source)
	{
		var lexer = new CvoloLexer(new AntlrInputStream(source));
		return lexer.GetAllTokens();
	}

	private static (IToken? Token, int Index) FindCoveringToken(IList<IToken> tokens, int position)
	{
		for (var i = 0; i < tokens.Count; i++)
		{
			var t = tokens[i];
			if (t.Type == TokenConstants.EOF)
				continue;

			// A cursor is a boundary between UTF-16 code units. At token.StartIndex it is
			// still before the token, not inside it; boundary handling below decides whether
			// the preceding word or a word starting here is the replacement target.
			if (t.StartIndex < position && position <= t.StopIndex)
				return (t, i);
		}

		return (null, -1);
	}

	private static (IToken? Token, int Index) FindWordTokenStartingAt(IList<IToken> tokens, int position)
	{
		for (var i = 0; i < tokens.Count; i++)
		{
			var t = tokens[i];
			if (t.Type == TokenConstants.EOF)
				continue;
			if (t.StartIndex == position && IsWordToken(t.Type))
				return (t, i);
		}

		return (null, -1);
	}

	private static (IToken? Token, int Index) FindPreviousToken(IList<IToken> tokens, int position)
	{
		for (var i = tokens.Count - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Type == TokenConstants.EOF)
				continue;
			if (t.StopIndex + 1 == position)
				return (t, i);
		}

		return (null, -1);
	}

	private static bool NeedsStatementTerminator(string source, int position)
	{
		for (var i = position; i < source.Length; i++)
		{
			if (char.IsWhiteSpace(source[i]))
				continue;

			return source[i] == '}';
		}

		return true;
	}

	private static bool PreviousSignificantTokenIsDot(IList<IToken> tokens, int beforeIndex)
	{
		for (var i = beforeIndex - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF)
				continue;
			if (t.Type == CvoloLexer.WS)
				continue;

			return t.Type == CvoloLexer.DOT;
		}

		return false;
	}

	private static bool LastSignificantTokenIsDot(IList<IToken> tokens, int position)
	{
		for (var i = tokens.Count - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF)
				continue;
			if (t.StopIndex >= position)
				continue;

			return t.Type == CvoloLexer.DOT;
		}

		return false;
	}

	private static bool PreviousSignificantTokenIsTypeMarker(IList<IToken> tokens, int beforeIndex)
	{
		for (var i = beforeIndex - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF)
				continue;
			if (t.Type == CvoloLexer.WS)
				continue;

			return IsTypeMarkerType(t.Type);
		}

		return false;
	}

	private static bool LastSignificantTokenIsTypeMarker(IList<IToken> tokens, int position)
	{
		for (var i = tokens.Count - 1; i >= 0; i--)
		{
			var t = tokens[i];
			if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF)
				continue;
			if (t.StopIndex >= position)
				continue;

			return IsTypeMarkerType(t.Type);
		}

		return false;
	}

	// Grammar authority (CvoloLexer.g4): only VAL/VAR declare an optional type slot, GLOBAL and ALIAS
	// are followed by a type, while REFVAR/REF bind a bare Identifier with no type slot.
	private static bool IsTypeMarkerType(int tokenType) => tokenType switch
	{
		CvoloLexer.VAL or CvoloLexer.VAR or CvoloLexer.GLOBAL or CvoloLexer.ALIAS => true,
		_ => false
	};

	private static bool IsCommentOrLiteralType(int tokenType) => tokenType switch
	{
		CvoloLexer.CharLiteral or CvoloLexer.BadEmptyCharLiteral or CvoloLexer.BadCharLiteral or
		CvoloLexer.RawStringLiteral or CvoloLexer.InterpolatedRawStringLiteral or CvoloLexer.BadRawStringLiteral or
		CvoloLexer.BadInterpolatedRawStringLiteral or CvoloLexer.InterpolatedStringLiteral or CvoloLexer.StringLiteral => true,
		CvoloLexer.LineComment or CvoloLexer.BlockComment => true,
		_ => false
	};

	private static bool IsWordToken(int tokenType)
	{
		if (tokenType >= CvoloLexer.VAL && tokenType <= CvoloLexer.CATCH)
			return true;
		return tokenType == CvoloLexer.Identifier;
	}

	private static int BackscanWordStart(string source, int from, int floor)
	{
		var i = from;
		while (i >= floor && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
			i--;
		return i + 1;
	}
}
