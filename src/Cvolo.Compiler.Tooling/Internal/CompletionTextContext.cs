using Antlr4.Runtime;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Lexical context of a completion request: whether the cursor is inside a comment or string/char
/// literal (which suppresses candidates), the replacement span covering the identifier or keyword
/// being typed (or a zero-length span), the typed prefix, and whether the previous significant token
/// indicates a type-only position.
/// Offsets and spans are UTF-16 based, matching <see cref="SourceText"/> and the lexer.
/// </summary>
internal sealed record CompletionTextContext(
	bool IsCommentOrLiteral,
	TextSpan ReplacementSpan,
	string Prefix,
	bool IsTypeContext)
{
	internal static CompletionTextContext Analyze(SourceText text, int position)
	{
		var source = text.ToString();
		var tokens = Lex(source);

		var (token, tokenIndex) = FindCoveringToken(tokens, position);

		if (token is not null)
		{
			if (IsCommentOrLiteralType(token.Type))
			{
				var start = BackscanWordStart(source, position - 1, token.StartIndex);
				return new CompletionTextContext(true, new TextSpan(start, position - start),
					source.Substring(start, position - start), false);
			}

			if (IsWordToken(token.Type))
			{
				var prefix = source.Substring(token.StartIndex, position - token.StartIndex);
				var tokenLength = token.StopIndex - token.StartIndex + 1;
				return new CompletionTextContext(false, new TextSpan(token.StartIndex, tokenLength),
					prefix, PreviousSignificantTokenIsTypeMarker(tokens, tokenIndex));
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
				var prefix = source.Substring(prev.StartIndex, position - prev.StartIndex);
				var tokenLength = prev.StopIndex - prev.StartIndex + 1;
				return new CompletionTextContext(false, new TextSpan(prev.StartIndex, tokenLength),
					prefix, PreviousSignificantTokenIsTypeMarker(tokens, prevIndex));
			}

			var (nextWord, nextWordIndex) = FindWordTokenStartingAt(tokens, position);
			if (nextWord is not null)
			{
				var tokenLength = nextWord.StopIndex - nextWord.StartIndex + 1;
				return new CompletionTextContext(false, new TextSpan(nextWord.StartIndex, tokenLength),
					string.Empty, PreviousSignificantTokenIsTypeMarker(tokens, nextWordIndex));
			}
		}

		return new CompletionTextContext(false, new TextSpan(position, 0), string.Empty,
			LastSignificantTokenIsTypeMarker(tokens, position));
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

		if (position > 0 && source[position - 1] == '.')
		{
			probeText = source.Insert(position, MemberProbeIdentifier);
			return true;
		}

		if (position > 0 && IsWordChar(source[position - 1]))
		{
			probeText = source.Insert(position, ";");
			return true;
		}

		var tokens = Lex(source);
		if (LastSignificantTokenIsTypeMarker(tokens, position))
		{
			probeText = source.Insert(position, MemberProbeIdentifier);
			return true;
		}

		probeText = string.Empty;
		return false;
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
