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
	internal static CompletionTextContext Analyze(SourceText text, int position)
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
