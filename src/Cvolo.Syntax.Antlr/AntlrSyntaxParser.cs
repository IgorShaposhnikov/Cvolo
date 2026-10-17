using System.Globalization;
using System.Text;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Syntax.Antlr;

public sealed class AntlrSyntaxParser : ISyntaxParser
{
	private readonly DiagnosticBag _diagnostics = new();
	private CompilationContext? _compilationContext;
	private int _malformedGenericSourceOffset = -1;

	// Spans ANTLR handed us that cannot be mapped onto the original source text (negative
	// offsets, or offsets/lengths beyond the buffer) for a non-EOF token. These are internal
	// faults, recorded for telemetry/tests and deliberately *not* relocated to EOF or 0 in the
	// outward diagnostics, so a broken coordinate never silently misplaces a red squiggle.
	private readonly List<TextSpan> _rejectedImpossibleSpans = new();

	// Unsuffixed integer magnitudes in (long.MaxValue, ulong.MaxValue] are valid only as the operand
	// of a direct unary minus (the signed-minimum exception). They are tracked here until either a
	// unary minus consumes them or the parse finishes and they are still unresolved.
	private readonly Dictionary<int, string> _pendingSignedMagnitudeOffsets = new();

	public DiagnosticBag Diagnostics => _diagnostics;

	/// <summary>
	/// Original-source spans the lexer/parser reported with coordinates that cannot exist in the
	/// source (non-EOF token with a negative/overflowing start or length). The corresponding
	/// outward diagnostic is omitted rather than relocated; this list is the test/telemetry hook.
	/// </summary>
	public IReadOnlyList<TextSpan> RejectedImpossibleSpans => _rejectedImpossibleSpans;

	public CompilationUnitSyntax? Parse(CompilationContext context)
	{
		_compilationContext = context;
		_pendingSignedMagnitudeOffsets.Clear();
		_malformedGenericSourceOffset = -1;
		_rejectedImpossibleSpans.Clear();
		var inputStream = new AntlrInputStream(context.Source);
		var lexer = new CvoloLexer(inputStream);

		// The lexer's default error listener writes "token recognition error" straight to the
		// console. That is invisible to the compiler driver (which reads the diagnostic bag) and
		// floods the language server's stderr on every reparse. Route lexer errors into the same
		// diagnostic pipeline as parser errors so they become real editor diagnostics instead.
		lexer.RemoveErrorListeners();
		lexer.AddErrorListener(new LexerErrorListener(_diagnostics, context, _rejectedImpossibleSpans));

		var tokenStream = new CommonTokenStream(lexer);
		tokenStream.Fill();
		// The grammar cannot parse a missing type head after a generic '<'. In the
		// adjacent spelling the lexer has already combined both characters into
		// LSHIFT, so ordinary ANTLR recovery can misclassify the remainder as a
		// shift expression. Detect malformed generic slots without changing lexer
		// tokenization or the meaning of expression shift operators.
		var invalidGeneric = FindMalformedGeneric(tokenStream.GetTokens());
		if (invalidGeneric is { } badGeneric)
		{
			_malformedGenericSourceOffset = badGeneric.Span.Start;
			_diagnostics.Report(context, badGeneric.Span, badGeneric.Message, badGeneric.Id);
		}

		// Recover only clearly separated malformed tokens, not general newlines:
		// declarations and expressions can legally continue across physical lines.
		// Diagnostics stay attached to the original offending word; ANTLR receives
		// a same-length view with that word blanked, preserving all later offsets.
		var standalone = FindStandaloneRecoveryTokens(tokenStream.GetTokens());
		foreach (var stray in standalone)
			_diagnostics.Report(context, stray.Span, stray.Message, DiagnosticIds.UnexpectedToken);

		var recoverySource = invalidGeneric is { } malformedType
			? MaskInvalidDeclaration(context.Source, tokenStream.GetTokens(), malformedType.Span.Start)
			: context.Source;
		foreach (var stray in standalone)
			recoverySource = MaskSourceRange(recoverySource, stray.Span.Start, stray.Span.End);

		if (recoverySource != context.Source)
		{
			var recoveryLexer = new CvoloLexer(new AntlrInputStream(recoverySource));
			recoveryLexer.RemoveErrorListeners();
			recoveryLexer.AddErrorListener(new LexerErrorListener(_diagnostics, context, _rejectedImpossibleSpans));
			tokenStream = new CommonTokenStream(recoveryLexer);
			tokenStream.Fill();
		}

		var parser = new CvoloParser(tokenStream);

		parser.RemoveErrorListeners();
		var errorListener = new SyntaxErrorListener(_diagnostics, context, tokenStream, _rejectedImpossibleSpans);
		parser.AddErrorListener(errorListener);
		if (invalidGeneric is { } malformed)
			errorListener.SuppressGenericRecovery(malformed.RecoveryStart, malformed.RecoveryEnd);

		var tree = parser.compilationUnit();

		ReportLeadingDotOutsideExtension(tree, tokenStream, errorListener);

		// Unterminated raw strings abort tree building (the surrounding statement is
		// missing its ';'), so surface them here from the raw token stream as CVL2000.
		foreach (var token in tokenStream.GetTokens())
		{
			if (token.Type == CvoloLexer.BadRawStringLiteral || token.Type == CvoloLexer.BadInterpolatedRawStringLiteral)
			{
				_diagnostics.Report(_compilationContext, SpanOf(token),
					"Unterminated raw string literal. Every `@\"` must have a matching closing quote.",
					DiagnosticIds.UnbalancedRawStringLiteral);
			}
		}

		// `defer` requires a statement or block body. A `defer;` or a `defer if (...)` (any token that
		// cannot begin a body) is a grammar error; surface it as CVL1065 so the missing-body case is actionable.
		var allTokens = tokenStream.GetTokens();
		for (var i = 0; i < allTokens.Count; i++)
		{
			if (allTokens[i].Type != CvoloLexer.DEFER)
				continue;

			var bodyIndex = NextVisibleIndex(allTokens, i);
			if (bodyIndex < 0)
				continue;

			var bodyToken = allTokens[bodyIndex].Type;
			var cannotBeginBody =
				bodyToken == CvoloLexer.SEMI ||
				bodyToken == CvoloLexer.IF ||
				bodyToken == CvoloLexer.WHILE ||
				bodyToken == CvoloLexer.FOR ||
				bodyToken == CvoloLexer.FOREACH ||
				bodyToken == CvoloLexer.RETURN ||
				bodyToken == CvoloLexer.SWITCH ||
				bodyToken == CvoloLexer.UNSAFE ||
				bodyToken == CvoloLexer.DEFER ||
				bodyToken == CvoloLexer.BREAK ||
				bodyToken == CvoloLexer.CONTINUE;

			if (!cannotBeginBody)
				continue;

			_diagnostics.Report(_compilationContext, SpanOf(allTokens[i]),
				"`defer` requires a statement or block body.",
				DiagnosticIds.DeferRequiresBody);
		}

		// Parse errors do not prevent tree recovery: ANTLR still produces a (partial) tree, and
		// consumers that tolerate partial syntax (the tooling) can analyze the declarations they can
		// recover. The compiler driver independently aborts on parser diagnostics, so compilation is
		// unchanged. Only if recovery cannot build a coherent unit do we return null as before.
		CompilationUnitSyntax? unit = null;
		try
		{
			unit = BuildCompilationUnit(tree, context);
		}
		catch (Exception) when (_diagnostics.HasErrors)
		{
			unit = null;
		}

		ReportPendingSignedMagnitudeOverflows();
		return unit;
	}

	private sealed record StandaloneRecoveryToken(TextSpan Span, string Message);

	/// <summary>
	/// Find a few unambiguous parser-cascade shapes that ANTLR otherwise
	/// reports on the following valid declaration or statement. All require a
	/// physical line break and a statement/declaration boundary: newline itself
	/// does not terminate statements in Cvolo.
	/// </summary>
	private static IReadOnlyList<StandaloneRecoveryToken> FindStandaloneRecoveryTokens(IList<IToken> tokens)
	{
		var visible = tokens.Where(t => t.Channel == Lexer.DefaultTokenChannel && t.Type != TokenConstants.EOF).ToArray();
		var recovery = new List<StandaloneRecoveryToken>();
		var braceDepth = 0;
		for (var i = 0; i < visible.Length; i++)
		{
			var current = visible[i];
			var previous = i > 0 ? visible[i - 1] : null;
			var next = i + 1 < visible.Length ? visible[i + 1] : null;
			var atBoundary = previous is null || previous.Type is CvoloLexer.SEMI or CvoloLexer.LBRACE or CvoloLexer.RBRACE;
			if (atBoundary && next is not null && next.Line > current.Line && current.StartIndex >= 0)
			{
				// A stray word before a built-in return type should not turn the
				// return-type keyword into an unexpected *function name*.
				var strayTopLevel = braceDepth == 0
					&& current.Type == CvoloLexer.Identifier
					&& IsPrimitiveDeclarationType(next.Type)
					&& i + 3 < visible.Length
					&& visible[i + 2].Type == CvoloLexer.Identifier
					&& visible[i + 3].Type == CvoloLexer.LPAREN;
				// A standalone primitive or identifier before a fresh var/val
				// declaration cannot legally form part of that declaration: VAR/VAL
				// are reserved tokens, not user-defined variable names. Recover on
				// the preceding word rather than swallowing the next declaration
				// into ANTLR's 'doublevar' or 'd0ovar' failure.
				var nextIsDeclaration = next.Type is CvoloLexer.VAR or CvoloLexer.VAL
					&& i + 2 < visible.Length
					&& (visible[i + 2].Type == CvoloLexer.Identifier
						|| IsPrimitiveDeclarationType(visible[i + 2].Type));
				var bareType = braceDepth > 0
					&& IsPrimitiveDeclarationType(current.Type)
					&& nextIsDeclaration;
				var bareIdentifier = braceDepth > 0
					&& current.Type == CvoloLexer.Identifier
					&& nextIsDeclaration;

				if (strayTopLevel || bareType || bareIdentifier)
				{
					var span = new TextSpan(current.StartIndex, current.StopIndex - current.StartIndex + 1);
					var message = strayTopLevel
						? $"Unexpected token '{current.Text}' before a function declaration."
						: bareType
							? $"Incomplete variable declaration: expected a name after '{current.Text}'."
							: $"Unexpected identifier '{current.Text}' before a new declaration. Complete the statement or remove the identifier.";
					recovery.Add(new StandaloneRecoveryToken(span, message));
				}
			}

			if (current.Type == CvoloLexer.LBRACE)
				braceDepth++;
			else if (current.Type == CvoloLexer.RBRACE && braceDepth > 0)
				braceDepth--;
		}
		return recovery;
	}

	private static bool IsPrimitiveDeclarationType(int tokenType) => tokenType is
		CvoloLexer.VOID or CvoloLexer.INT or CvoloLexer.UINT
		or CvoloLexer.LONG or CvoloLexer.ULONG or CvoloLexer.SHORT
		or CvoloLexer.USHORT or CvoloLexer.BYTE or CvoloLexer.SBYTE
		or CvoloLexer.NINT or CvoloLexer.NUINT or CvoloLexer.FLOAT
		or CvoloLexer.DOUBLE or CvoloLexer.BOOL or CvoloLexer.STRING
		or CvoloLexer.CHAR;

	/// <summary>
	/// Remove the one broken top-level declaration from ANTLR's recovery input
	/// while preserving all offsets, line breaks and subsequent declarations.
	/// The original source remains authoritative for the root diagnostic.
	/// In particular a recovered generic must never become an ordinary type.
	/// </summary>
	private static string MaskInvalidDeclaration(string source, IList<IToken> tokens, int invalidPosition)
	{
		var visible = tokens.Where(t => t.Channel == Lexer.DefaultTokenChannel && t.Type != TokenConstants.EOF).ToArray();
		var memberStart = -1;
		var depth = 0;
		foreach (var token in visible)
		{
			if (memberStart < 0 && depth == 0)
				memberStart = token.StartIndex;
			if (token.Type == CvoloLexer.LBRACE)
				depth++;
			else if (token.Type == CvoloLexer.RBRACE && depth > 0)
				depth--;
			if (depth != 0 || (token.Type != CvoloLexer.SEMI && token.Type != CvoloLexer.RBRACE))
				continue;
			if (memberStart <= invalidPosition && invalidPosition <= token.StopIndex)
				return MaskSourceRange(source, memberStart, token.StopIndex + 1);
			memberStart = -1;
		}
		if (memberStart >= 0 && invalidPosition >= memberStart)
			return MaskSourceRange(source, memberStart, source.Length);
		return source;
	}

	private static string MaskSourceRange(string source, int start, int end)
	{
		var characters = source.ToCharArray();
		for (var i = Math.Max(0, start); i < Math.Min(end, characters.Length); i++)
			if (characters[i] is not ('\r' or '\n'))
				characters[i] = ' ';
		return new string(characters);
	}

	/// <summary>
	/// True when a potential generic owner lives inside an earlier unmatched
	/// generic list. The next '>' is then an outer closer, not a surplus one.
	/// </summary>
	private static bool HasEnclosingGenericAngle(IToken[] tokens, int before)
	{
		var depth = 0;
		for (var i = 0; i < before; i++)
		{
			var kind = tokens[i].Type;
			if (kind is CvoloLexer.SEMI or CvoloLexer.LBRACE or CvoloLexer.RBRACE)
				depth = 0;
			else if (kind == CvoloLexer.LT && i > 0 && tokens[i - 1].Type is CvoloLexer.Identifier or CvoloLexer.GT)
				depth++;
			else if (kind == CvoloLexer.GT && depth > 0)
				depth--;
		}
		return depth > 0;
	}

	private sealed record MalformedGeneric(TextSpan Span, string Message, string Id, int RecoveryStart, int RecoveryEnd);

	/// <summary>
	/// Inspect actual lexer tokens (including maximal-munch LSHIFT) for malformed
	/// named generic arguments. This does not split or reclassify shift tokens:
	/// valid comparison and shift expressions keep the original lexer stream.
	/// </summary>
	private static MalformedGeneric? FindMalformedGeneric(IList<IToken> tokens)
	{
		var visible = tokens.Where(t => t.Channel == Lexer.DefaultTokenChannel).ToArray();
		for (var i = 1; i < visible.Length - 1; i++)
		{
			var opener = visible[i];
			if (visible[i - 1].Type != CvoloLexer.Identifier ||
				opener.Type is not (CvoloLexer.LT or CvoloLexer.LSHIFT))
				continue;

			var next = visible[i + 1];
			// A plain `a << b` is an expression shift. Only the generic-like
			// spelling with argument separators / closers is a type error.
			if (opener.Type == CvoloLexer.LSHIFT)
			{
				if (!LooksLikeGenericTail(visible, i + 1))
					continue;
				return InvalidSlot(opener.StartIndex + 1, opener.StartIndex, visible, i,
					"Expected a type argument after '<'; an unnamed nested generic type is not valid.");
			}

			if (next.Type == CvoloLexer.LT)
				return InvalidSlot(next.StartIndex, opener.StartIndex, visible, i,
					"Expected a type argument after '<'; a type name is required before nested '<'.");
			if (next.Type is CvoloLexer.GT or CvoloLexer.COMMA)
				return InvalidSlot(next.StartIndex, opener.StartIndex, visible, i,
					"Expected a type argument after '<'; generic argument slots cannot be empty.",
					next.Type == CvoloLexer.GT ? DiagnosticIds.UnexpectedToken : DiagnosticIds.ExpectedToken);

			// A top-level generic-list closer is the only kind of '>' that this
			// local scan recognizes; we never reinterpret GT in an expression.
			var depth = 1;
			var sawComma = false;
			for (var j = i + 1; j < visible.Length; j++)
			{
				var token = visible[j];
				if (token.Type == CvoloLexer.LT) { depth++; continue; }
				if (token.Type == CvoloLexer.GT)
				{
					depth--;
					if (depth != 0) continue;
					if (j + 2 < visible.Length && visible[j + 1].Type == CvoloLexer.GT
						&& visible[j + 2].Type is (CvoloLexer.LBRACE or CvoloLexer.SEMI)
						&& !HasEnclosingGenericAngle(visible, i))
						return InvalidSlot(visible[j + 1].StartIndex, opener.StartIndex, visible, i,
							"Unexpected '>' after the completed generic type argument list.", DiagnosticIds.UnexpectedToken);
					break;
				}
				if (token.Type == CvoloLexer.COMMA && depth == 1)
				{
					sawComma = true;
					if (j + 1 < visible.Length && visible[j + 1].Type is (CvoloLexer.GT or CvoloLexer.COMMA or CvoloLexer.LT))
						return InvalidSlot(visible[j + 1].StartIndex, opener.StartIndex, visible, i,
							"Expected a type argument after ',' in the generic argument list.");
				}
				if (token.Type == CvoloLexer.LSHIFT && depth > 0 && sawComma)
					return InvalidSlot(token.StartIndex + 1, opener.StartIndex, visible, i,
						"Expected a type name before a nested generic argument list.");
				if (token.Type is CvoloLexer.LBRACE or CvoloLexer.SEMI or TokenConstants.EOF)
				{
					if (sawComma)
					{
						var previous = visible[j - 1];
						var insertion = Math.Max(opener.StartIndex + 1, previous.StopIndex + 1);
						return InvalidSlot(insertion, opener.StartIndex, visible, i,
							"Expected '>' to close the generic type argument list.", zeroWidth: true);
					}
					break;
				}
				if (token.Type == CvoloLexer.RPAREN && sawComma)
					break;
			}
		}
		return null;
	}

	private static bool LooksLikeGenericTail(IToken[] tokens, int start)
	{
		var sawArgumentSeparator = false;
		for (var i = start; i < tokens.Length && i < start + 64; i++)
		{
			var current = tokens[i].Type;
			if (current == CvoloLexer.COMMA)
				sawArgumentSeparator = true;
			if (current == CvoloLexer.GT && sawArgumentSeparator && i + 1 < tokens.Length
				&& tokens[i + 1].Type is (CvoloLexer.COMMA or CvoloLexer.GT
					or CvoloLexer.LBRACE or CvoloLexer.LPAREN or CvoloLexer.SEMI))
				return true;
			if (current is CvoloLexer.SEMI or TokenConstants.EOF or CvoloLexer.LBRACE
				or CvoloLexer.LPAREN or CvoloLexer.RPAREN)
				return false;
		}
		return false;
	}

	private static MalformedGeneric InvalidSlot(int at, int ownerStart, IToken[] tokens, int ownerIndex,
		string message, string id = DiagnosticIds.ExpectedToken, bool zeroWidth = false)
	{
		var end = tokens.Length == 0 ? at : tokens[^1].StopIndex + 1;
		// Suppress dependent ANTLR diagnostics only until the current statement's
		// semicolon. Any later statement remains independently diagnosable.
		for (var j = ownerIndex + 1; j < tokens.Length; j++)
		{
			if (tokens[j].Type == CvoloLexer.SEMI) { end = tokens[j].StopIndex; break; }
			if (tokens[j].Type == TokenConstants.EOF) { end = tokens[j].StartIndex; break; }
		}
		return new MalformedGeneric(new TextSpan(Math.Max(0, at), zeroWidth ? 0 : 1),
			message, id, ownerStart, end);
	}

	// An unsuffixed integer magnitude in (long.MaxValue, ulong.MaxValue] is legal only when it is the
	// operand of a direct unary minus. Any such literal that survives parsing is not part of a valid
	// signed-minimum expression and must be reported as too large.
	private void ReportPendingSignedMagnitudeOverflows()
	{
		if (_compilationContext is null || _pendingSignedMagnitudeOffsets.Count == 0)
			return;

		foreach (var (offset, text) in _pendingSignedMagnitudeOffsets)
		{
			_diagnostics.Report(
				_compilationContext,
				new TextSpan(offset, text.Length),
				$"Integer literal `{text}` is too large for type `ulong`.",
				DiagnosticIds.IntegerLiteralTooLarge);
		}
	}

	// First index after <paramref name="startIndex"/> whose token is on the default channel
	// (whitespace is skipped by the lexer; comments live on the COMMENTS channel).
	private static int NextVisibleIndex(IList<IToken> tokens, int startIndex)
	{
		for (var i = startIndex + 1; i < tokens.Count; i++)
		{
			if (tokens[i].Channel == TokenConstants.DefaultChannel)
				return i;
		}

		return -1;
	}

	// Token types that can end a type, i.e. can be immediately followed by a leading dot in
	// `ReturnType .Name(...)`. Mirrors the `type` rule: a `primitiveType` keyword, a
	// `qualifiedName`/`Identifier`, or one of the postfix forms (`[]`, `<...>`, `*`, `?`).
	private static bool EndsType(int tokenType) => tokenType is
		CvoloLexer.Identifier
		or CvoloLexer.INT or CvoloLexer.UINT or CvoloLexer.LONG or CvoloLexer.ULONG
		or CvoloLexer.SHORT or CvoloLexer.USHORT or CvoloLexer.BYTE or CvoloLexer.SBYTE
		or CvoloLexer.NINT or CvoloLexer.NUINT
		or CvoloLexer.FLOAT or CvoloLexer.DOUBLE
		or CvoloLexer.GT or CvoloLexer.RBRACK or CvoloLexer.STAR or CvoloLexer.QMARK;

	/// <summary>
	/// A leading dot is only meaningful inside an extension block. The top-level
	/// <c>functionDeclaration</c> rule has no DOT alternative, so a stray leading dot surfaces as a
	/// bare syntax error; upgrade it to a dedicated, actionable diagnostic.
	/// </summary>
	private void ReportLeadingDotOutsideExtension(
		CvoloParser.CompilationUnitContext tree,
		CommonTokenStream tokenStream,
		SyntaxErrorListener errorListener)
	{
		if (errorListener.RejectedDotIndices.Count == 0)
			return;

		// Character ranges covered by extension blocks; a rejected dot inside one of them is some
		// other (already reported) syntax error and must not be relabelled.
		var extensionRanges = new List<(int Start, int Stop)>();
		foreach (var ext in CollectExtensionDeclarations(tree))
			extensionRanges.Add((ext.Start.StartIndex, ext.Stop.StopIndex));

		var tokens = tokenStream.GetTokens();
		for (var i = 0; i < tokens.Count; i++)
		{
			var dot = tokens[i];
			if (dot.Type != CvoloLexer.DOT || dot.Channel != TokenConstants.DefaultChannel)
				continue;

			if (!errorListener.RejectedDotIndices.Contains(dot.StartIndex))
				continue;

			if (extensionRanges.Any(r => dot.StartIndex >= r.Start && dot.StartIndex <= r.Stop))
				continue;

			var nameIndex = NextVisibleIndex(tokens, i);
			if (nameIndex < 0 || tokens[nameIndex].Type != CvoloLexer.Identifier)
				continue;

			var typeIndex = i - 1;
			while (typeIndex >= 0 && tokens[typeIndex].Channel != TokenConstants.DefaultChannel)
				typeIndex--;
			if (typeIndex < 0 || !EndsType(tokens[typeIndex].Type))
				continue;

			_diagnostics.Report(_compilationContext, SpanOf(dot),
				"Associated function declarations are only allowed inside an extension block.",
				DiagnosticIds.AssociatedFunctionOutsideExtension);
		}
	}

	private static IEnumerable<CvoloParser.ExtensionDeclarationContext> CollectExtensionDeclarations(CvoloParser.CompilationUnitContext tree)
	{
		var collector = new ExtensionDeclarationCollector();
		Antlr4.Runtime.Tree.ParseTreeWalker.Default.Walk(collector, tree);
		return collector.Found;
	}

	private sealed class ExtensionDeclarationCollector : CvoloParserBaseListener
	{
		public List<CvoloParser.ExtensionDeclarationContext> Found { get; } = [];

		public override void EnterExtensionDeclaration(CvoloParser.ExtensionDeclarationContext context) => Found.Add(context);
	}

	private CompilationUnitSyntax BuildCompilationUnit(CvoloParser.CompilationUnitContext context, CompilationContext compilationContext)
	{
		var usings = new List<UsingDirectiveSyntax>();
		foreach (var u in context.usingDirective())
			usings.Add(BuildUsingDirective(u));

		NamespaceDeclarationSyntax? nsDecl = null;
		if (context.namespaceDeclaration() is { } ns)
			nsDecl = BuildNamespaceDeclaration(ns);

		var members = new List<SyntaxNode>();
		if (nsDecl is null)
		{
			foreach (var decl in context.declaration())
			{
				var node = BuildDeclaration(decl);
				if (node is not null)
					members.Add(node);
			}
		}

		// Pass compilationContext as the second argument
		return new CompilationUnitSyntax(SpanOf(context), compilationContext, usings, nsDecl, members);
	}

	private SyntaxNode? BuildDeclaration(CvoloParser.DeclarationContext context)
	{
		// Fail-closed fallback if malformed input could not be masked before parsing.
		// Never expose its synthetic generic type to the binder or emitter.
		if (_malformedGenericSourceOffset >= 0 && context.Start is { } start
			&& start.StartIndex <= _malformedGenericSourceOffset
			&& (context.Stop?.StopIndex ?? -1) >= _malformedGenericSourceOffset)
			return null;
		if (context.functionDeclaration() is { } func)
			return BuildFunctionDeclaration(func);
		if (context.externDeclaration() is { } ext)
			return BuildExternDeclaration(ext);
		if (context.externGlobalDeclaration() is { } extGlobal)
			return BuildExternGlobalDeclaration(extGlobal);
		if (context.externBlockDeclaration() is { } extBlock)
			return BuildExternBlockDeclaration(extBlock);
		if (context.exposeExternBlockDeclaration() is { } exposeBlock)
			return BuildExposeExternBlockDeclaration(exposeBlock);
		if (context.exposeExternExportDeclaration() is { } exposeExport)
			return BuildExposeExternExportDeclaration(exposeExport);
		if (context.structDeclaration() is { } structDecl)
			return BuildStructDeclaration(structDecl);
		if (context.unionDeclaration() is { } unionDecl)
			return BuildUnionDeclaration(unionDecl);
		if (context.enumDeclaration() is { } enumDecl)
			return BuildEnumDeclaration(enumDecl);
		if (context.extensionDeclaration() is { } extensionDecl)
			return BuildExtensionDeclaration(extensionDecl);
		if (context.interfaceDeclaration() is { } interfaceDecl)
			return BuildInterfaceDeclaration(interfaceDecl);
		if (context.protocolDeclaration() is { } protocolDecl)
			return BuildProtocolDeclaration(protocolDecl);
		if (context.globalVariableDeclaration() is { } globalDecl)
			return BuildGlobalVariableDeclaration(globalDecl);
		if (context.aliasDeclaration() is { } aliasDecl)
			return BuildAliasDeclaration(aliasDecl);
		if (context.delegateDeclaration() is { } delegateDecl)
			return BuildDelegateDeclaration(delegateDecl);
		if (context.delegateBlockDeclaration() is { } delegateBlock)
			return BuildDelegateBlockDeclaration(delegateBlock);
		return null;
	}

	private TypeAliasDeclarationSyntax BuildAliasDeclaration(CvoloParser.AliasDeclarationContext context)
	{
		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		return new TypeAliasDeclarationSyntax(SpanOf(context), context.Identifier().GetText(), GetTypeName(context.type()), generics);
	}

	private DelegateDeclarationSyntax BuildDelegateDeclaration(CvoloParser.DelegateDeclarationContext context)
	{
		var isNative = context.UNSAFE() is not null;

		string? callingConvention = null;
		if (context.callingConvention() is { } cc)
		{
			callingConvention = cc.StringLiteral().GetText();
			if (callingConvention.Length >= 2)
				callingConvention = callingConvention[1..^1];
		}

		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		var parameters = new List<ParameterSyntax>();
		if (context.delegateParameterList() is { } paramList)
		{
			foreach (var p in paramList.delegateParameter())
			{
				// Parse receiver-shaped delegate parameters instead of rejecting them in the
				// grammar. Declaration validation owns the semantic CVL1301 diagnostic, which
				// keeps native/safe delegate behavior deterministic and testable.
				if (p.receiverVarParameter() is { } rv)
				{
					parameters.Add(new ParameterSyntax(SpanOf(p), "refvar", rv.Identifier().GetText()));
					continue;
				}

				if (p.receiverRefParameter() is { } rr)
				{
					parameters.Add(new ParameterSyntax(SpanOf(p), "ref", rr.Identifier().GetText()));
					continue;
				}

				parameters.Add(new ParameterSyntax(SpanOf(p), GetTypeName(p.type()), p.Identifier().GetText()));
			}
		}

		var nameToken = context.Identifier().Symbol;
		var returnTypeCtx = context.type();

		return new DelegateDeclarationSyntax(
			SpanOf(context),
			GetTypeName(returnTypeCtx),
			nameToken.Text,
			generics,
			parameters,
			GetVisibilityModifier(context.visibilityModifier()),
			isNative,
			callingConvention,
			nameSpan: SpanOf(nameToken),
			returnTypeSpan: SpanOf(returnTypeCtx));
	}

	private DelegateBlockDeclarationSyntax BuildDelegateBlockDeclaration(CvoloParser.DelegateBlockDeclarationContext context)
	{
		var callingConvention = context.callingConvention()?.StringLiteral()?.GetText();
		if (callingConvention is not null && callingConvention.Length >= 2)
			callingConvention = callingConvention[1..^1];

		var delegates = new List<DelegateDeclarationSyntax>();
		foreach (var delegateCtx in context.delegateDeclaration())
			delegates.Add(BuildDelegateDeclaration(delegateCtx));

		return new DelegateBlockDeclarationSyntax(SpanOf(context), callingConvention, delegates);
	}

	private GlobalVariableDeclarationSyntax BuildGlobalVariableDeclaration(CvoloParser.GlobalVariableDeclarationContext context)
	{
		var isMutable = context.VAR() is not null;
		var type = GetTypeName(context.type());
		var name = context.Identifier().GetText();
		ExpressionSyntax? initializer = null;
		if (context.expression() is { } exprCtx)
			initializer = BuildExpression(exprCtx);
		return new GlobalVariableDeclarationSyntax(SpanOf(context), type, name, initializer, isMutable, GetVisibilityModifier(context.visibilityModifier()));
	}

	private static Visibility? GetVisibilityModifier(CvoloParser.VisibilityModifierContext? context) =>
		context?.GetText() switch
		{
			"private" => Visibility.Private,
			"internal" => Visibility.Internal,
			"public" => Visibility.Public,
			_ => null
		};

	private ExternDeclarationSyntax BuildExternDeclaration(CvoloParser.ExternDeclarationContext context)
	{
		var returnType = GetReturnTypeName(context.returnType());
		var name = context.Identifier().GetText();
		var parameters = new List<ParameterSyntax>();
		var isVariadic = false;
		if (context.externParameterList() is { } paramList)
		{
			foreach (var param in paramList.externParameter())
			{
				if (param.ELLIPSIS() is not null)
					isVariadic = true;
				else
					parameters.Add(new ParameterSyntax(SpanOf(param), param.type().GetText(), param.Identifier().GetText()));
			}
		}

		return new ExternDeclarationSyntax(SpanOf(context), returnType, name, parameters, isVariadic, GetVisibilityModifier(context.visibilityModifier()));
	}

	private ExternBlockSyntax BuildExternBlockDeclaration(CvoloParser.ExternBlockDeclarationContext context)
	{
		var attributes = BuildAttributeList(context.attributeList());
		var callingConvention = context.callingConvention()?.StringLiteral()?.GetText();
		if (callingConvention is not null)
		{
			// Strip the surrounding double quotes to recover the raw "C" / "system" value.
			callingConvention = callingConvention.Length >= 2 ? callingConvention[1..^1] : callingConvention;
		}

		var functions = new List<ExternBlockFunctionSyntax>();
		var globals = new List<GlobalVariableDeclarationSyntax>();
		foreach (var member in context.externBlockMember())
		{
			if (member.externBlockFunction() is { } funcCtx)
				functions.Add(BuildExternBlockFunction(funcCtx));
			else if (member.externBlockGlobal() is { } globalCtx)
				globals.Add(BuildExternBlockGlobal(globalCtx, callingConvention));
		}

		return new ExternBlockSyntax(SpanOf(context), attributes, callingConvention, functions, GetVisibilityModifier(context.visibilityModifier()), globals);
	}

	private GlobalVariableDeclarationSyntax BuildExternBlockGlobal(CvoloParser.ExternBlockGlobalContext context, string? blockConvention)
	{
		var isMutable = context.VAR() is not null;
		var type = GetTypeName(context.type());
		var name = context.Identifier().GetText();
		return new GlobalVariableDeclarationSyntax(
			SpanOf(context),
			type,
			name,
			initializer: null,
			isMutable,
			GetVisibilityModifier(context.visibilityModifier()),
			attributes: BuildAttributeList(context.attributeList()),
			isForeign: true,
			callingConvention: blockConvention);
	}

	private GlobalVariableDeclarationSyntax BuildExternGlobalDeclaration(CvoloParser.ExternGlobalDeclarationContext context)
	{
		var isMutable = context.VAR() is not null;
		var type = GetTypeName(context.type());
		var name = context.Identifier().GetText();

		var callingConvention = context.callingConvention()?.StringLiteral()?.GetText();
		if (callingConvention is not null && callingConvention.Length >= 2)
			callingConvention = callingConvention[1..^1];

		return new GlobalVariableDeclarationSyntax(
			SpanOf(context),
			type,
			name,
			initializer: null,
			isMutable,
			GetVisibilityModifier(context.visibilityModifier()),
			attributes: BuildAttributeList(context.attributeList()),
			isForeign: true,
			callingConvention: callingConvention);
	}

	private ExternBlockFunctionSyntax BuildExternBlockFunction(CvoloParser.ExternBlockFunctionContext context)
	{
		var returnType = GetReturnTypeName(context.returnType());
		var name = context.Identifier().GetText();
		var parameters = new List<ParameterSyntax>();
		var isVariadic = false;
		if (context.externParameterList() is { } paramList)
		{
			foreach (var param in paramList.externParameter())
			{
				if (param.ELLIPSIS() is not null)
					isVariadic = true;
				else
					parameters.Add(new ParameterSyntax(SpanOf(param), param.type().GetText(), param.Identifier().GetText()));
			}
		}

		return new ExternBlockFunctionSyntax(SpanOf(context), returnType, name, parameters, isVariadic, BuildAttributeList(context.attributeList()), GetVisibilityModifier(context.visibilityModifier()));
	}

	private ExposeExternBlockSyntax BuildExposeExternBlockDeclaration(CvoloParser.ExposeExternBlockDeclarationContext context)
	{
		var attributes = BuildAttributeList(context.attributeList());
		var callingConvention = GetRawCallingConvention(context.callingConvention());

		var functions = new List<FunctionDeclarationSyntax>();
		foreach (var funcCtx in context.exposeExternFunction())
			functions.Add(BuildExposeExternFunction(funcCtx));

		return new ExposeExternBlockSyntax(SpanOf(context), attributes, callingConvention, functions, GetVisibilityModifier(context.visibilityModifier()));
	}

	private ExposeExternBlockSyntax BuildExposeExternExportDeclaration(CvoloParser.ExposeExternExportDeclarationContext context)
	{
		var callingConvention = GetRawCallingConvention(context.callingConvention());
		var blockBody = context.blockStatement() is { } blockCtx ? BuildBlockStatement(blockCtx) : null;

		var function = new FunctionDeclarationSyntax(
			SpanOf(context),
			GetReturnTypeName(context.returnType()),
			context.Identifier().GetText(),
			[],
			BuildParameterList(context.parameterList()),
			blockBody!,
			nameSpan: SpanOf(context.Identifier().Symbol),
			returnTypeSpan: SpanOf(context.returnType()));

		return new ExposeExternBlockSyntax(SpanOf(context), [], callingConvention, [function], null);
	}

	private FunctionDeclarationSyntax BuildExposeExternFunction(CvoloParser.ExposeExternFunctionContext context)
	{
		var generics = new List<string>();
		if (context.typeList() is { } typeListCtx)
		{
			foreach (var t in typeListCtx.type())
				generics.Add(GetTypeName(t));
		}

		var blockBody = context.blockStatement() is { } blockCtx ? BuildBlockStatement(blockCtx) : null;

		return new FunctionDeclarationSyntax(
			SpanOf(context),
			GetReturnTypeName(context.returnType()),
			context.Identifier().GetText(),
			generics,
			BuildParameterList(context.parameterList()),
			blockBody!,
			BuildAttributeList(context.attributeList()),
			null,
			ReceiverContract.None,
			GetVisibilityModifier(context.visibilityModifier()),
			nameSpan: SpanOf(context.Identifier().Symbol),
			returnTypeSpan: SpanOf(context.returnType()));
	}

	private List<ParameterSyntax> BuildParameterList(CvoloParser.ParameterListContext? paramList)
	{
		var parameters = new List<ParameterSyntax>();
		if (paramList is null)
			return parameters;

		foreach (var param in paramList.parameter())
			parameters.Add(BuildParameter(param));
		return parameters;
	}

	private static string? GetRawCallingConvention(CvoloParser.CallingConventionContext? context)
	{
		var callingConvention = context?.StringLiteral()?.GetText();
		if (callingConvention is not null)
		{
			// Strip the surrounding double quotes to recover the raw "C" / "system" value.
			callingConvention = callingConvention.Length >= 2 ? callingConvention[1..^1] : callingConvention;
		}

		return callingConvention;
	}

	private FunctionDeclarationSyntax BuildFunctionDeclaration(CvoloParser.FunctionDeclarationContext context)
		=> BuildFunctionDeclarationCore(
			declarationContext: context,
			returnTypeCtx: context.returnType(),
			nameCtx: context.builtinCapableIdentifier(),
			typeListCtx: context.typeList(),
			parameterListCtx: context.parameterList(),
			blockCtx: context.blockStatement(),
			attributeListCtx: context.attributeList(),
			functionModifierCtx: context.functionModifier(),
			callingConventionCtx: context.callingConvention(),
			visibilityCtx: context.visibilityModifier(),
			builtinToken: context.BUILTIN()?.Symbol,
			leadingDot: null,
			whereClauses: context.whereClause());

	private FunctionDeclarationSyntax BuildExtensionFunctionDeclaration(CvoloParser.ExtensionFunctionDeclarationContext context)
		=> BuildFunctionDeclarationCore(
			declarationContext: context,
			returnTypeCtx: context.returnType(),
			nameCtx: context.builtinCapableIdentifier(),
			typeListCtx: context.typeList(),
			parameterListCtx: context.parameterList(),
			blockCtx: context.blockStatement(),
			attributeListCtx: context.attributeList(),
			functionModifierCtx: context.functionModifier(),
			callingConventionCtx: context.callingConvention(),
			visibilityCtx: context.visibilityModifier(),
			builtinToken: context.BUILTIN()?.Symbol,
			leadingDot: context.DOT()?.Symbol,
			whereClauses: null);

	private FunctionDeclarationSyntax BuildFunctionDeclarationCore(
		Antlr4.Runtime.ParserRuleContext declarationContext,
		CvoloParser.ReturnTypeContext returnTypeCtx,
		CvoloParser.BuiltinCapableIdentifierContext nameCtx,
		CvoloParser.TypeListContext? typeListCtx,
		CvoloParser.ParameterListContext? parameterListCtx,
		CvoloParser.BlockStatementContext? blockCtx,
		IEnumerable<CvoloParser.AttributeListContext> attributeListCtx,
		CvoloParser.FunctionModifierContext? functionModifierCtx,
		CvoloParser.CallingConventionContext? callingConventionCtx,
		CvoloParser.VisibilityModifierContext? visibilityCtx,
		Antlr4.Runtime.IToken? builtinToken,
		Antlr4.Runtime.IToken? leadingDot,
		IEnumerable<CvoloParser.WhereClauseContext?>? whereClauses)
	{
		var returnType = GetReturnTypeName(returnTypeCtx);
		var name = nameCtx.GetText();
		var isAssociated = leadingDot is not null;

		// Parse optional generic parameters list (which can now contain concrete types like <int>)
		var generics = new List<string>();
		if (typeListCtx is not null)
		{
			foreach (var t in typeListCtx.type())
				generics.Add(GetTypeName(t));
		}

		var parameters = new List<ParameterSyntax>();
		var receiver = ReceiverContract.None;
		if (parameterListCtx is not null)
		{
			foreach (var param in parameterListCtx.parameter())
			{
				if (TryGetReceiverContract(param, out var contract, out var receiverName))
				{
					if (isAssociated)
					{
						ReportParseError(
							param,
							$"Associated function '{name}' cannot declare an instance receiver.",
							DiagnosticIds.AssociatedFunctionWithReceiver);
						continue;
					}

					if (parameters.Count > 0)
					{
						ReportParseError(param, "Receiver parameter ('refvar this' / 'ref this') must be the first parameter of the method.", DiagnosticIds.ReceiverParameterRefvarThisRefThisMustBeTheFirstParameterOfT);
						continue;
					}

					if (receiverName != "this")
					{
						ReportParseError(param, $"Receiver parameter must be named 'this' (found '{receiverName}').", DiagnosticIds.ReceiverParameterMustBeNamedThisFound);
						continue;
					}

					receiver = contract;
					continue;
				}

				parameters.Add(BuildParameter(param));
			}
		}

		var body = blockCtx is not null ? BuildBlockStatement(blockCtx) : null;
		var attributes = BuildAttributeList(attributeListCtx);

		// Extract optional function modifier (unsafe / unbound)
		SafetyTier? modifier = null;
		if (functionModifierCtx is not null)
			modifier = functionModifierCtx.GetText() == "unsafe" ? SafetyTier.Unsafe : SafetyTier.Unbound;

		// Optional native-ABI calling convention slot: 'unsafe "C"' / 'unsafe "system"' before the
		// return type. Requires the function modifier to be absent (the convention keyword is
		// parsed from the leading UNSAFE token).
		string? callingConvention = null;
		if (callingConventionCtx is { } ccCtx)
		{
			callingConvention = ccCtx.StringLiteral().GetText();
			if (callingConvention.Length >= 2)
				callingConvention = callingConvention[1..^1];
		}

		var (genericDefaults, genericConstraints) = BuildWhereClauses(whereClauses);

		var function = new FunctionDeclarationSyntax(
			SpanOf(declarationContext), returnType, name, generics, parameters, body, attributes, modifier, receiver,
			GetVisibilityModifier(visibilityCtx),
			nameSpan: SpanOf(nameCtx),
			returnTypeSpan: SpanOf(returnTypeCtx),
			callingConvention: callingConvention,
			bindingKind: isAssociated ? FunctionBindingKind.Associated : FunctionBindingKind.Default,
			genericParameterConstraints: genericConstraints,
			genericParameterDefaults: genericDefaults);
		if (builtinToken is not null)
		{
			function.IsBuiltin = true;
			function.BuiltinSpan = SpanOf(builtinToken);
		}

		return function;
	}

	private bool TryGetReceiverContract(CvoloParser.ParameterContext context, out ReceiverContract contract, out string receiverName)
	{
		if (context.receiverVarParameter() is { } rv)
		{
			contract = ReceiverContract.Refvar;
			receiverName = rv.Identifier().GetText();
			return true;
		}

		if (context.receiverRefParameter() is { } rr)
		{
			contract = ReceiverContract.Ref;
			receiverName = rr.Identifier().GetText();
			return true;
		}

		contract = ReceiverContract.None;
		receiverName = "";
		return false;
	}

	private void ReportParseError(Antlr4.Runtime.ParserRuleContext context, string message, string diagnosticId)
	{
		if (_compilationContext is not null)
			_diagnostics.Report(_compilationContext, SpanOf(context), message, diagnosticId);
	}

	private List<AttributeSyntax> BuildAttributeList(IEnumerable<CvoloParser.AttributeListContext> contexts)
	{
		var attributes = new List<AttributeSyntax>();
		foreach (var listCtx in contexts)
		{
			foreach (var attrCtx in listCtx.attribute())
			{
				var args = new List<ExpressionSyntax>();
				var argNames = new List<string?>();
				if (attrCtx.attributeArgumentList() is { } argList)
				{
					foreach (var arg in argList.attributeArgument())
					{
						args.Add(BuildExpression(arg.expression()));
						argNames.Add(arg.Identifier()?.GetText() ?? null);
					}
				}

				attributes.Add(new AttributeSyntax(SpanOf(attrCtx), attrCtx.qualifiedName().GetText(), args, argNames));
			}
		}

		return attributes;
	}

	private (Dictionary<string, string> Defaults, Dictionary<string, List<string>> Constraints) BuildWhereClauses(IEnumerable<CvoloParser.WhereClauseContext?> whereClauses)
	{
		var defaults = new Dictionary<string, string>();
		var constraints = new Dictionary<string, List<string>>();

		if (whereClauses is null)
			return (defaults, constraints);

		foreach (var whereCtx in whereClauses)
		{
			if (whereCtx is null)
				continue;

			foreach (var constraint in whereCtx.whereConstraint())
			{
				if (constraint is CvoloParser.DefaultWhereConstraintContext defCtx)
				{
					var paramName = defCtx.Identifier().GetText();
					var typeName = GetTypeName(defCtx.type());
					defaults[paramName] = typeName;
				}
				else if (constraint is CvoloParser.InterfaceWhereConstraintContext typeCtx)
				{
					var paramName = typeCtx.Identifier().GetText();
					var typeName = GetTypeName(typeCtx.type());
					if (!constraints.TryGetValue(paramName, out var list))
					{
						list = [];
						constraints[paramName] = list;
					}

					list.Add(typeName);
				}
			}
		}

		return (defaults, constraints);
	}

	private StructDeclarationSyntax BuildStructDeclaration(CvoloParser.StructDeclarationContext context)
	{
		var name = context.Identifier().GetText();

		// Parse optional generic parameters list: <T>
		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		var (defaults, constraints) = BuildWhereClauses(context.whereClause());

		var fields = new List<StructFieldSyntax>();
		foreach (var field in context.structField())
			fields.Add(new StructFieldSyntax(SpanOf(field), GetTypeName(field.type()), field.Identifier().GetText(), GetVisibilityModifier(field.visibilityModifier())));
		var embeddedType = context.EMBED() is not null && context.qualifiedName() is { } embedCtx ? embedCtx.GetText() : null;
		var structAttributes = BuildAttributeList(context.attributeList());
		var structDeclaration = new StructDeclarationSyntax(
			SpanOf(context),
			name,
			generics,
			fields,
			embeddedType,
			structAttributes,
			GetVisibilityModifier(context.visibilityModifier()),
			defaults,
			constraints);
		if (context.BUILTIN() is { } builtinToken)
		{
			structDeclaration.IsBuiltin = true;
			structDeclaration.BuiltinSpan = SpanOf(builtinToken.Symbol);
		}

		return structDeclaration;
	}

	private ParameterSyntax BuildParameter(CvoloParser.ParameterContext context)
	{
		var type = GetTypeName(context.type());
		var identifier = context.Identifier();
		var name = identifier is null ? string.Empty : identifier.GetText();

		if (name.Length > 0 && name[0] == '<')
		{
			name = string.Empty;
		}

		if (string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(name))
		{
			type = name;
			name = string.Empty;
		}
		else if (!string.IsNullOrWhiteSpace(name) && string.Equals(name, type, StringComparison.Ordinal))
		{
			name = string.Empty;
		}

		var attributes = BuildAttributeList(context.attributeList());
		return new ParameterSyntax(SpanOf(context), type, name, attributes);
	}

	private BlockStatementSyntax BuildBlockStatement(CvoloParser.BlockStatementContext context)
	{
		var statements = new List<SyntaxNode>();
		foreach (var stmt in context.statement())
		{
			var node = BuildStatement(stmt);
			if (node is not null)
				statements.Add(node);
		}

		return new BlockStatementSyntax(SpanOf(context), statements);
	}

	private SyntaxNode? BuildStatement(CvoloParser.StatementContext context)
	{
		if (context.returnStatement() is { } ret)
			return BuildReturnStatement(ret);
		if (context.expressionStatement() is { } exprStmt)
		{
			var expr = BuildExpression(exprStmt.expression());
			return new ExpressionStatementSyntax(SpanOf(exprStmt), expr);
		}
		if (context.variableDeclaration() is { } varDecl)
			return BuildVariableDeclaration(varDecl);
		if (context.blockStatement() is { } block)
			return BuildBlockStatement(block);
		if (context.ifStatement() is { } ifStmt)
			return BuildIfStatement(ifStmt);
		if (context.switchStatement() is { } sw)
			return BuildSwitchStatement(sw);
		if (context.whileStatement() is { } whileStmt)
			return BuildWhileStatement(whileStmt);
		if (context.forStatement() is { } forStmt)
			return BuildForStatement(forStmt);
		if (context.forEachStatement() is { } forEachStmt)
			return BuildForEachStatement(forEachStmt);
		if (context.labeledBlockStatement() is { } labeledBlock)
			return BuildLabeledBlockStatement(labeledBlock);
		if (context.unsafeBlockStatement() is { } unsafeBlock)
			return new UnsafeBlockStatementSyntax(SpanOf(unsafeBlock), BuildBlockStatement(unsafeBlock.blockStatement()));
		if (context.deferStatement() is { } defer)
			return BuildDeferStatement(defer);
		if (context.tryStatement() is { } tryStmt)
			return BuildTryStatement(tryStmt);
		if (context.controlExitStatement() is { } ctrlExit)
			return BuildControlExitStatement(ctrlExit);
		return null;
	}

	private ReturnStatementSyntax BuildReturnStatement(CvoloParser.ReturnStatementContext context)
	{
		ExpressionSyntax? expr = null;
		if (context.expression() is { } exprCtx)
			expr = BuildExpression(exprCtx);
		return new ReturnStatementSyntax(SpanOf(context), expr);
	}

	private ExpressionStatementSyntax BuildExpressionStatement(CvoloParser.ExpressionStatementContext context)
	{
		var expr = BuildExpression(context.expression());
		return new ExpressionStatementSyntax(SpanOf(context), expr);
	}

	private DeferStatementSyntax BuildDeferStatement(CvoloParser.DeferStatementContext context)
	{
		var label = context.Identifier()?.GetText();
		if (context.blockStatement() is { } block)
		{
			var body = BuildBlockStatement(block);
			return new DeferStatementSyntax(SpanOf(context), body, label);
		}

		if (context.expressionStatement() is { } exprStmt)
		{
			var body = BuildExpressionStatement(exprStmt);
			return new DeferStatementSyntax(SpanOf(context), body);
		}

		throw new InvalidOperationException("Defer statement has no valid body (expression or block).");
	}

	private LabeledBlockStatementSyntax BuildLabeledBlockStatement(CvoloParser.LabeledBlockStatementContext context)
	{
		return new LabeledBlockStatementSyntax(SpanOf(context), context.Identifier().GetText(), BuildBlockStatement(context.blockStatement()));
	}

	private TryStatementSyntax BuildTryStatement(CvoloParser.TryStatementContext context)
	{
		var body = BuildBlockStatement(context.blockStatement());
		var clauses = new List<CatchClauseSyntax>();
		foreach (var clause in context.catchClause())
		{
			switch (clause)
			{
				case CvoloParser.CatchBareClauseContext bare:
					clauses.Add(new CatchClauseSyntax(SpanOf(bare), null, null, null, isBare: true,
						BuildBlockStatement(bare.blockStatement())));
					break;
				case CvoloParser.CatchTypedClauseContext typed:
					clauses.Add(new CatchClauseSyntax(SpanOf(typed), typed.qualifiedName().GetText(), null,
						typed.Identifier().GetText(), isBare: false, BuildBlockStatement(typed.blockStatement())));
					break;
				case CvoloParser.CatchValueOrTypeClauseContext valueOrType:
					// Ambiguous between a value-pattern `catch (EnumType.Variant)` and a type-pattern
					// `catch (Qualified.Type)`. The raw qualified name is kept whole; TryCatchRewriter
					// classifies it against the declaration index (known enum receivers are value patterns).
					clauses.Add(new CatchClauseSyntax(SpanOf(valueOrType), valueOrType.qualifiedName().GetText(),
						null, null, isBare: false, BuildBlockStatement(valueOrType.blockStatement())));
					break;
			}
		}

		return new TryStatementSyntax(SpanOf(context), body, clauses,
			context.finallyClause() is { } finallyCtx ? BuildBlockStatement(finallyCtx.blockStatement()) : null);
	}

	private ExpressionSyntax BuildExpression(CvoloParser.ExpressionContext? context)
	{
		// ANTLR error recovery can leave an expression child null while the enclosing statement is
		// still being built (a bare identifier on its own line, an unfinished ternary `x ?`, an
		// unfinished `x ??`). Building it unconditionally used to throw, which dropped the whole
		// document's parse unit and wiped its semantic tokens and codelens. Synthesize a
		// zero-length placeholder so the AST stays well-formed and the analyzer recovers until the
		// missing expression is typed in.
		if (context is null)
			return new IdentifierExpressionSyntax(new TextSpan(0, 0), "");

		switch (context)
		{
			case CvoloParser.StringLiteralExpressionContext strCtx:
				{
					var raw = strCtx.StringLiteral().GetText();

					if (raw.StartsWith("\\\"") && raw.EndsWith("\\\""))
					{
						raw = raw[2..^2];
					}

					var value = DecodeString(raw);
					return new StringLiteralExpressionSyntax(SpanOf(strCtx), value);
				}
			case CvoloParser.InterpolatedStringExpressionContext interCtx:
				return new InterpolatedStringExpressionSyntax(SpanOf(interCtx), interCtx.InterpolatedStringLiteral().GetText());
			case CvoloParser.RawStringExpressionContext rawCtx:
				return new StringLiteralExpressionSyntax(SpanOf(rawCtx), DecodeRawString(rawCtx.RawStringLiteral().GetText()));
			case CvoloParser.InterpolatedRawStringExpressionContext rawInterCtx:
				return new InterpolatedStringExpressionSyntax(SpanOf(rawInterCtx), rawInterCtx.InterpolatedRawStringLiteral().GetText());
			case CvoloParser.AsmExpressionContext asmCtx:
				{
					var options = AsmOptions.None;
					foreach (var opt in asmCtx.asmOption())
						options |= ParseAsmOption(opt);

					string? resultType = null;
					if (asmCtx.type() is { } asmTypeCtx)
						resultType = GetTypeName(asmTypeCtx);

					var template = DecodeString(asmCtx.StringLiteral().GetText());

					var operands = new List<AsmOperandSyntax>();
					var clobbers = new List<string>();
					foreach (var arg in asmCtx.asmArgument())
					{
						switch (arg)
						{
							case CvoloParser.AsmOperandArgumentContext operandArgCtx:
								{
									string? name = null;
									if (operandArgCtx.Identifier() is { } idTok)
										name = idTok.GetText();

									var constraint = DecodeString(operandArgCtx.StringLiteral().GetText());
									var expr = BuildExpression(operandArgCtx.expression());
									operands.Add(new AsmOperandSyntax(SpanOf(operandArgCtx), name, constraint, expr));
									break;
								}
							case CvoloParser.AsmClobberArgumentContext clobberArgCtx:
								clobbers.Add(DecodeString(clobberArgCtx.StringLiteral().GetText()));
								break;
							case CvoloParser.AsmOptionArgumentContext optionArgCtx:
								options |= ParseAsmOption(optionArgCtx.asmOption());
								break;
						}
					}

					return new AsmExpressionSyntax(SpanOf(asmCtx), template, operands, clobbers, options, resultType);
				}
			case CvoloParser.NameofExpressionContext nameofCtx:
				return new NameofExpressionSyntax(SpanOf(nameofCtx), BuildExpression(nameofCtx.expression()));
			case CvoloParser.TypeofExpressionContext typeofCtx:
				return new TypeofExpressionSyntax(SpanOf(typeofCtx), GetTypeName(typeofCtx.type()));
			case CvoloParser.SizeofExpressionContext sizeofCtx:
				return new SizeofExpressionSyntax(SpanOf(sizeofCtx), GetTypeName(sizeofCtx.type()));
			case CvoloParser.AlignofExpressionContext alignofCtx:
				return new AlignofExpressionSyntax(SpanOf(alignofCtx), GetTypeName(alignofCtx.type()));
			case CvoloParser.OffsetofExpressionContext offsetofCtx:
				{
					var members = new List<OffsetofMemberSyntax>();
					foreach (var identifier in offsetofCtx.memberDesignator().Identifier())
					{
						var token = identifier.Symbol;
						var start = Math.Max(token.StartIndex, 0);
						var end = token.StopIndex >= start ? token.StopIndex + 1 : start;
						members.Add(new OffsetofMemberSyntax(token.Text, TextSpan.FromBounds(start, end)));
					}

					return new OffsetofExpressionSyntax(SpanOf(offsetofCtx), GetTypeName(offsetofCtx.type()), members);
				}
			case CvoloParser.BadRawStringExpressionContext badRawCtx:
				{
					ReportParseError(badRawCtx, "Unterminated raw string literal. Every `@\"` must have a matching closing quote.", DiagnosticIds.UnbalancedRawStringLiteral);
					return new StringLiteralExpressionSyntax(SpanOf(badRawCtx), "");
				}
			case CvoloParser.CharLiteralExpressionContext charCtx:
				{
					var text = charCtx.CharLiteral().GetText();
					var (value, outOfRangeHex) = DecodeCharLiteral(text[1..^1]);
					if (outOfRangeHex is not null)
						ReportParseError(charCtx, $"Hexadecimal escape sequence `\\x{outOfRangeHex}` is out of range (0-255).", DiagnosticIds.HexEscapeOutOfRange);

					return new CharacterLiteralExpressionSyntax(SpanOf(charCtx), value);
				}

			case CvoloParser.BadEmptyCharLiteralExpressionContext badEmptyCtx:
				{
					ReportParseError(badEmptyCtx, "Empty character literal.", DiagnosticIds.EmptyCharacterLiteral);
					return new CharacterLiteralExpressionSyntax(SpanOf(badEmptyCtx), '\0');
				}

			case CvoloParser.BadCharLiteralExpressionContext badCharCtx:
				{
					var text = badCharCtx.BadCharLiteral().GetText();
					var content = text[1..^1];
					if (content[0] == '\\')
						ReportParseError(badCharCtx, $"Unknown escape sequence `{content}`.", DiagnosticIds.UnknownEscapeSequence);
					else
						ReportParseError(badCharCtx, "Character literal must be a single character (or escape sequence).", DiagnosticIds.MultipleCharacterLiteral);

					return new CharacterLiteralExpressionSyntax(SpanOf(badCharCtx), '\0');
				}

			case CvoloParser.IntegerLiteralExpressionContext intCtx:
				{
					var text = intCtx.IntegerLiteral().GetText();
					var result = DecodeIntegerLiteral(text);
					if (result.OverflowType is not null)
						ReportParseError(intCtx, $"Integer literal `{text}` is too large for type `{result.OverflowType}`.", DiagnosticIds.IntegerLiteralTooLarge);
					else if (result.LiteralType == "long" && result.Value > long.MaxValue)
						_pendingSignedMagnitudeOffsets[SpanOf(intCtx).Start] = text;

					return new IntegerLiteralExpressionSyntax(SpanOf(intCtx), result.Value, result.LiteralType);
				}

			case CvoloParser.BadIntegerSuffixExpressionContext badSuffixCtx:
				{
					var suffix = GetAlphaTail(badSuffixCtx.BadIntegerSuffix().GetText());
					ReportParseError(badSuffixCtx, $"Invalid integer suffix `{suffix}`. Expected `U`, `L`, or `UL`.", DiagnosticIds.InvalidIntegerSuffix);
					return new IntegerLiteralExpressionSyntax(SpanOf(badSuffixCtx), 0);
				}

			case CvoloParser.BadLeadingUnderscoreExpressionContext badLeadingCtx:
				{
					ReportParseError(badLeadingCtx, "Digit separators cannot appear at the start or end of a literal.", DiagnosticIds.InvalidDigitSeparator);
					return new IntegerLiteralExpressionSyntax(SpanOf(badLeadingCtx), 0);
				}

			case CvoloParser.BadIntegerSeparatorExpressionContext badSeparatorCtx:
				{
					ReportParseError(badSeparatorCtx, "Digit separators cannot appear at the start or end of a literal.", DiagnosticIds.InvalidDigitSeparator);
					return new IntegerLiteralExpressionSyntax(SpanOf(badSeparatorCtx), 0);
				}

			case CvoloParser.DoubleLiteralExpressionContext dblCtx:
				{
					var (value, isFloat) = DecodeDoubleLiteral(dblCtx.DoubleLiteral().GetText());
					return new DoubleLiteralExpressionSyntax(SpanOf(dblCtx), value, isFloat);
				}

			case CvoloParser.BadDoubleLiteralExpressionContext badDblCtx:
				{
					var suffix = badDblCtx.BadDoubleLiteral().GetText()[^1];
					ReportParseError(badDblCtx, $"Unknown floating-point suffix `{suffix}`. Expected `f`, `F`, `d`, or `D`.", DiagnosticIds.UnknownFloatSuffix);
					return new DoubleLiteralExpressionSyntax(SpanOf(badDblCtx), 0);
				}
			case CvoloParser.BooleanLiteralExpressionContext boolCtx:
				return new BooleanLiteralExpressionSyntax(SpanOf(boolCtx), boolCtx.TRUE() is not null);
			case CvoloParser.NullLiteralExpressionContext nullCtx:
				return new NullLiteralExpressionSyntax(SpanOf(nullCtx));
			case CvoloParser.IdentifierExpressionContext idCtx:
				return new IdentifierExpressionSyntax(SpanOf(idCtx), idCtx.Identifier().GetText());
			case CvoloParser.LambdaExpressionContext lambdaCtx:
				{
					var captureMode = lambdaCtx.lambdaCaptureMode()?.GetText() switch
					{
						"move" => LambdaCaptureMode.Move,
						"ref" => LambdaCaptureMode.Ref,
						"refvar" => LambdaCaptureMode.RefVar,
						_ => LambdaCaptureMode.Default,
					};

					var parameters = new List<LambdaParameterSyntax>();
					if (lambdaCtx.lambdaParameterList() is { } lambdaParams)
					{
						foreach (var lambdaParam in lambdaParams.lambdaParameter())
						{
							if (lambdaParam.type() is { } typeCtx)
							{
								var nameNode = lambdaParam.children.OfType<ITerminalNode>().Last(t => t.Symbol.Type == CvoloParser.Identifier);
								parameters.Add(new LambdaParameterSyntax(SpanOf(lambdaParam), nameNode.GetText(), GetTypeName(typeCtx)));
							}
							else
							{
								var nameNode = lambdaParam.children.OfType<ITerminalNode>().Single(t => t.Symbol.Type == CvoloParser.Identifier);
								parameters.Add(new LambdaParameterSyntax(SpanOf(lambdaParam), nameNode.GetText()));
							}
						}
					}

					if (lambdaCtx.blockStatement() is { } blockBodyCtx)
						return new LambdaExpressionSyntax(SpanOf(lambdaCtx), captureMode, parameters, LambdaBodyKind.Block, null, BuildBlockStatement(blockBodyCtx));

					return new LambdaExpressionSyntax(SpanOf(lambdaCtx), captureMode, parameters, LambdaBodyKind.Expression, BuildExpression(lambdaCtx.expression()), null);
				}
			case CvoloParser.ParenthesizedExpressionContext parenCtx:
				return BuildExpression(parenCtx.expression());
			case CvoloParser.ParenthesizedStructInitializerContext parenCtx:
				{
					var initializers = new List<MemberInitializerSyntax>();
					if (parenCtx.parenFieldInitializerList() is { } listCtx)
					{
						foreach (var memberInit in listCtx.parenFieldInitializer())
						{
							var memberName = memberInit.Identifier().GetText();
							var expr = BuildExpression(memberInit.expression());
							initializers.Add(new MemberInitializerSyntax(SpanOf(memberInit), memberName, expr));
						}
					}

					return new ParenthesizedStructInitializerExpressionSyntax(SpanOf(parenCtx), initializers);
				}
			case CvoloParser.CallExpressionContext callCtx:
				{
					var funcName = callCtx.qualifiedName().GetText();

					var typeArgs = new List<string>();
					if (callCtx.typeList() is { } typeListCtx)
					{
						foreach (var t in typeListCtx.type())
							typeArgs.Add(GetTypeName(t));
					}

					var args = new List<ExpressionSyntax>();
					if (callCtx.argumentList() is { } argList)
					{
						foreach (var arg in argList.expression())
							args.Add(BuildExpression(arg));
					}

					TextSpan? argumentListSpan = null;
					if (callCtx.LPAREN() is { } leftParen && callCtx.RPAREN() is { } rightParen)
						argumentListSpan = TextSpan.FromBounds(leftParen.Symbol.StartIndex, rightParen.Symbol.StopIndex + 1);

					return new CallExpressionSyntax(SpanOf(callCtx), funcName, typeArgs, args, argumentListSpan);
				}

			case CvoloParser.GenericTypeQualifiedCallExpressionContext genericCallCtx:
				{
					// A generic owner type may be written before the member separator
					// ('Result<int, MyError>.Ok(42)'). The owner is resolved semantically, so the call keeps
					// the owner and member as one dotted path exactly like any other type-qualified call.
					var ownerTypeList = genericCallCtx.typeList(0);
					var funcName = $"{genericCallCtx.qualifiedName().GetText()}<{string.Join(", ", ownerTypeList.type().Select(GetTypeName))}>.{genericCallCtx.builtinCapableIdentifier().GetText()}";

					// The owner's type arguments are part of the owner path, not of the member's; only a
					// second list is the member's own explicit type argument list.
					var typeArgs = new List<string>();
					if (genericCallCtx.typeList().Length > 1)
					{
						foreach (var t in genericCallCtx.typeList(1).type())
							typeArgs.Add(GetTypeName(t));
					}

					var args = new List<ExpressionSyntax>();
					if (genericCallCtx.argumentList() is { } genericArgList)
					{
						foreach (var arg in genericArgList.expression())
							args.Add(BuildExpression(arg));
					}

					TextSpan? genericArgumentListSpan = null;
					if (genericCallCtx.LPAREN() is { } genericLeftParen && genericCallCtx.RPAREN() is { } genericRightParen)
						genericArgumentListSpan = TextSpan.FromBounds(genericLeftParen.Symbol.StartIndex, genericRightParen.Symbol.StopIndex + 1);

					return new CallExpressionSyntax(SpanOf(genericCallCtx), funcName, typeArgs, args, genericArgumentListSpan);
				}

			case CvoloParser.UnaryMinusExpressionContext unaryMinus:
				{
					var operand = BuildExpression(unaryMinus.expression());
					if (operand is IntegerLiteralExpressionSyntax { LiteralType: "long" } signedMin
						&& _pendingSignedMagnitudeOffsets.Remove(signedMin.Span.Start))
					{
						return new UnaryExpressionSyntax(SpanOf(unaryMinus), "-", signedMin);
					}

					return new UnaryExpressionSyntax(SpanOf(unaryMinus), "-", operand);
				}
			case CvoloParser.UnaryPlusExpressionContext unaryPlus:
				return new UnaryExpressionSyntax(SpanOf(unaryPlus), "+", BuildExpression(unaryPlus.expression()));
			case CvoloParser.LogicalNotExpressionContext notCtx:
				return new UnaryExpressionSyntax(SpanOf(notCtx), "!", BuildExpression(notCtx.expression()));
			case CvoloParser.CastExpressionContext castCtx:
				{
					var inner = BuildExpression(castCtx.expression());
					return new UnaryExpressionSyntax(SpanOf(castCtx), $"({GetTypeName(castCtx.type())})", inner);
				}
			case CvoloParser.MultiplicativeExpressionContext multCtx:
				return new BinaryExpressionSyntax(SpanOf(multCtx), BuildExpression(multCtx.expression(0)), multCtx.GetChild(1).GetText(), BuildExpression(multCtx.expression(1)));
			case CvoloParser.AdditiveExpressionContext addCtx:
				return new BinaryExpressionSyntax(SpanOf(addCtx), BuildExpression(addCtx.expression(0)), addCtx.GetChild(1).GetText(), BuildExpression(addCtx.expression(1)));
			case CvoloParser.RelationalExpressionContext relCtx:
				return new BinaryExpressionSyntax(SpanOf(relCtx), BuildExpression(relCtx.expression(0)), relCtx.GetChild(1).GetText(), BuildExpression(relCtx.expression(1)));
			case CvoloParser.EqualityExpressionContext eqCtx:
				return new BinaryExpressionSyntax(SpanOf(eqCtx), BuildExpression(eqCtx.expression(0)), eqCtx.GetChild(1).GetText(), BuildExpression(eqCtx.expression(1)));
			case CvoloParser.IsPatternExpressionContext isCtx:
				return new IsPatternExpressionSyntax(
					SpanOf(isCtx),
					BuildExpression(isCtx.expression()),
					isCtx.Identifier(0).GetText(),
					isCtx.Identifier().Length > 1 ? isCtx.Identifier(1).GetText() : null);
			case CvoloParser.LogicalAndExpressionContext andCtx:
				return new BinaryExpressionSyntax(SpanOf(andCtx), BuildExpression(andCtx.expression(0)), "&&", BuildExpression(andCtx.expression(1)));
			case CvoloParser.LogicalOrExpressionContext orCtx:
				return new BinaryExpressionSyntax(SpanOf(orCtx), BuildExpression(orCtx.expression(0)), "||", BuildExpression(orCtx.expression(1)));
			case CvoloParser.AssignmentExpressionContext assignCtx:
				return new BinaryExpressionSyntax(SpanOf(assignCtx), BuildExpression(assignCtx.expression(0)), "=", BuildExpression(assignCtx.expression(1)));
			case CvoloParser.CompoundAssignmentExpressionContext compoundCtx:
				{
					var left = BuildExpression(compoundCtx.expression(0));
					var opAssign = compoundCtx.GetChild(1).GetText();
					var right = BuildExpression(compoundCtx.expression(1));
					var op = opAssign[..^1];
					var mathExpr = new BinaryExpressionSyntax(SpanOf(compoundCtx), left, op, right);
					return new BinaryExpressionSyntax(SpanOf(compoundCtx), left, "=", mathExpr);
				}
			case CvoloParser.PostfixIncrementExpressionContext incCtx:
				return new UnaryExpressionSyntax(SpanOf(incCtx), "++_postfix", BuildExpression(incCtx.expression()));
			case CvoloParser.PostfixDecrementExpressionContext decCtx:
				return new UnaryExpressionSyntax(SpanOf(decCtx), "--_postfix", BuildExpression(decCtx.expression()));
			case CvoloParser.BitwiseNotExpressionContext notCtx:
				return new UnaryExpressionSyntax(SpanOf(notCtx), "~", BuildExpression(notCtx.expression()));
			case CvoloParser.LeftShiftExpressionContext leftCtx:
				return new BinaryExpressionSyntax(SpanOf(leftCtx), BuildExpression(leftCtx.expression(0)), "<<", BuildExpression(leftCtx.expression(1)));
			case CvoloParser.RightShiftExpressionContext rightCtx:
				return new BinaryExpressionSyntax(SpanOf(rightCtx), BuildExpression(rightCtx.expression(0)), ">>", BuildExpression(rightCtx.expression(1)));
			case CvoloParser.ArithmeticRightShiftExpressionContext arithCtx:
				return new BinaryExpressionSyntax(SpanOf(arithCtx), BuildExpression(arithCtx.expression(0)), ">>>", BuildExpression(arithCtx.expression(1)));
			case CvoloParser.BitwiseAndExpressionContext andCtx:
				return new BinaryExpressionSyntax(SpanOf(andCtx), BuildExpression(andCtx.expression(0)), "&", BuildExpression(andCtx.expression(1)));
			case CvoloParser.BitwiseXorExpressionContext xorCtx:
				return new BinaryExpressionSyntax(SpanOf(xorCtx), BuildExpression(xorCtx.expression(0)), "^", BuildExpression(xorCtx.expression(1)));
			case CvoloParser.BitwiseOrExpressionContext orCtx:
				return new BinaryExpressionSyntax(SpanOf(orCtx), BuildExpression(orCtx.expression(0)), "|", BuildExpression(orCtx.expression(1)));
			case CvoloParser.PrefixIncrementExpressionContext preIncCtx:
				return new UnaryExpressionSyntax(SpanOf(preIncCtx), "++_prefix", BuildExpression(preIncCtx.expression()));
			case CvoloParser.PrefixDecrementExpressionContext preDecCtx:
				return new UnaryExpressionSyntax(SpanOf(preDecCtx), "--_prefix", BuildExpression(preDecCtx.expression()));
			case CvoloParser.MemberAccessExpressionContext memberCtx:
				{
					var left = BuildExpression(memberCtx.expression());
					var memberName = memberCtx.Identifier().GetText();
					return new MemberAccessExpressionSyntax(SpanOf(memberCtx), left, memberName);
				}
			case CvoloParser.ArrowMemberAccessExpressionContext arrowCtx:
				{
					var left = BuildExpression(arrowCtx.expression());
					var memberName = arrowCtx.Identifier().GetText();
					return new MemberAccessExpressionSyntax(SpanOf(arrowCtx), left, memberName, "->");
				}
			case CvoloParser.DereferenceExpressionContext derefCtx:
				return new UnaryExpressionSyntax(SpanOf(derefCtx), "*", BuildExpression(derefCtx.expression()));
			case CvoloParser.AddressOfExpressionContext addrCtx:
				return new UnaryExpressionSyntax(SpanOf(addrCtx), "&", BuildExpression(addrCtx.expression()));
			case CvoloParser.BorrowExpressionContext borrowCtx:
				{
					var expr = BuildExpression(borrowCtx.expression());
					var isMutable = borrowCtx.GetText().StartsWith("refvar");
					return new BorrowExpressionSyntax(SpanOf(borrowCtx), expr, isMutable);
				}
			case CvoloParser.StructInitializationExpressionContext structInitCtx:
				{
					var structName = structInitCtx.Identifier().GetText();

					if (structInitCtx.typeList() is { } typeListCtx)
					{
						var args = typeListCtx.type().Select(GetTypeName);
						structName = $"{structName}<{string.Join(", ", args)}>";
					}

					var initializers = new List<MemberInitializerSyntax>();
					if (structInitCtx.bracedFieldInitializerList() is { } listCtx)
					{
						foreach (var memberInit in listCtx.bracedFieldInitializer())
						{
							var memberName = memberInit.Identifier().GetText();
							var expr = BuildExpression(memberInit.expression());
							initializers.Add(new MemberInitializerSyntax(SpanOf(memberInit), memberName, expr));
						}
					}

					return new StructInitializationExpressionSyntax(SpanOf(structInitCtx), structName, initializers);
				}
			case CvoloParser.HeapAllocationExpressionContext heapCtx:
				return new HeapAllocationExpressionSyntax(SpanOf(heapCtx), BuildExpression(heapCtx.expression()));
			case CvoloParser.HeapArrayAllocationExpressionContext heapArrCtx:
				{
					var typeName = GetTypeName(heapArrCtx.type());
					var countExpr = BuildExpression(heapArrCtx.expression());
					return new HeapArrayAllocationExpressionSyntax(SpanOf(heapArrCtx), typeName, countExpr);
				}
			case CvoloParser.IndexExpressionContext idxCtx:
				return new IndexExpressionSyntax(SpanOf(idxCtx), BuildExpression(idxCtx.expression(0)), BuildExpression(idxCtx.expression(1)));
			case CvoloParser.ArrayInitializationExpressionContext arrInitCtx:
				{
					var elements = new List<ExpressionSyntax>();
					foreach (var exprCtx in arrInitCtx.expression())
					{
						elements.Add(BuildExpression(exprCtx));
					}

					return new ArrayInitializationExpressionSyntax(SpanOf(arrInitCtx), elements);
				}
			case CvoloParser.CatchExpressionContext catchCtx:
				{
					if (catchCtx.expression(1) is CvoloParser.LambdaExpressionContext catchLambda &&
						catchLambda.lambdaCaptureMode() is null &&
						catchLambda.blockStatement() is { } catchLambdaBody &&
						catchLambda.lambdaParameterList() is { } catchLambdaParams &&
						catchLambdaParams.lambdaParameter().Length == 1 &&
						catchLambdaParams.lambdaParameter()[0].type() is null)
					{
						var catchLambdaName = catchLambdaParams.lambdaParameter()[0].children
							.OfType<ITerminalNode>()
							.Single(t => t.Symbol.Type == CvoloParser.Identifier)
							.GetText();

						return new CatchExpressionSyntax(
							SpanOf(catchCtx),
							BuildExpression(catchCtx.expression(0)),
							null,
							new CatchLambdaExpressionSyntax(SpanOf(catchLambda), catchLambdaName, BuildBlockStatement(catchLambdaBody)));
					}

					return new CatchExpressionSyntax(SpanOf(catchCtx), BuildExpression(catchCtx.expression(0)), BuildExpression(catchCtx.expression(1)));
				}
			case CvoloParser.CatchLambdaExpressionContext catchLambdaCtx:
				return new CatchExpressionSyntax(
					SpanOf(catchLambdaCtx),
					BuildExpression(catchLambdaCtx.expression()),
					null,
					new CatchLambdaExpressionSyntax(
						SpanOf(catchLambdaCtx),
						catchLambdaCtx.Identifier().GetText(),
						BuildBlockStatement(catchLambdaCtx.blockStatement())));
			case CvoloParser.OptionCoalescingExpressionContext coalesceCtx:
				return BuildCoalesceExpression(coalesceCtx);
			case CvoloParser.OptionalChainExpressionContext chainCtx:
				{
					var chainReceiver = BuildExpression(chainCtx.expression());

					// The invocation form is identified by its parentheses, not by the presence of an
					// argument list: `x?.M()` is a call with zero arguments, not a field read.
					List<ExpressionSyntax>? chainArguments = null;
					if (chainCtx.LPAREN() is not null)
					{
						chainArguments = [];
						if (chainCtx.argumentList() is { } chainArgList)
						{
							foreach (var arg in chainArgList.expression())
								chainArguments.Add(BuildExpression(arg));
						}
					}

					return new OptionalChainExpressionSyntax(
						SpanOf(chainCtx),
						chainReceiver,
						chainCtx.Identifier().GetText(),
						chainArguments);
				}
			case CvoloParser.TernaryExpressionContext ternaryCtx:
				{
					var cond = BuildOptionalOperand(ternaryCtx.expression(0), ternaryCtx);
					var thenExpr = BuildOptionalOperand(ternaryCtx.expression(1), ternaryCtx);
					var elseExpr = BuildOptionalOperand(ternaryCtx.expression(2), ternaryCtx);
					return new TernaryExpressionSyntax(SpanOf(ternaryCtx), cond, thenExpr, elseExpr);
				}
			case CvoloParser.VoidLiteralExpressionContext voidCtx:
				return new VoidLiteralExpressionSyntax(SpanOf(voidCtx));
			case CvoloParser.DefaultExpressionContext defaultCtx:
				return new DefaultExpressionSyntax(SpanOf(defaultCtx), defaultCtx.type() is { } defaultTypeCtx ? GetTypeName(defaultTypeCtx) : null);
			default:
				return new IdentifierExpressionSyntax(SpanOf(context), context.GetText());
		}

		string DecodeString(string literal)
		{
			var inner = literal[1..^1];
			var sb = new StringBuilder(inner.Length);
			for (var i = 0; i < inner.Length; i++)
			{
				var c = inner[i];
				if (c != '\\' || i + 1 >= inner.Length)
				{
					sb.Append(c);
					continue;
				}

				var next = inner[i + 1];
				switch (next)
				{
					case 'n': sb.Append('\n'); i++; break;
					case 't': sb.Append('\t'); i++; break;
					case 'r': sb.Append('\r'); i++; break;
					case '"': sb.Append('"'); i++; break;
					case '\\': sb.Append('\\'); i++; break;
					case '0': sb.Append('\0'); i++; break;
					case 'x' or 'X':
						{
							var start = i + 2;
							var hexEnd = start;
							while (hexEnd < inner.Length && Uri.IsHexDigit(inner[hexEnd]))
								hexEnd++;
							if (hexEnd > start)
							{
								try
								{
									sb.Append((char)Convert.ToInt32(inner[start..hexEnd], 16));
								}
								catch (OverflowException)
								{
									sb.Append('?');
								}
								i = hexEnd - 1;
							}
							else
							{
								sb.Append('\\');
							}
							break;
						}
					default: sb.Append('\\'); break;
				}
			}

			return sb.ToString();
		}
	}

	/// <summary>
	/// Builds an Option fallback expression, enforcing the right-associativity the grammar cannot
	/// express.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>expression</c> is a single left-recursive rule, so its binary alternative for the fallback
	/// operator is left-associative by construction. The operator is specified as
	/// right-associative, so a left-nested fallback chain is rotated here: <c>a ?? b ?? c</c> becomes
	/// <c>a ?? (b ?? c)</c>. The operand trees are otherwise untouched.
	/// </para>
	/// <para>
	/// The rotation keys off the parse context, not the built tree, because both spellings of a
	/// left-nested chain produce the same left node: an explicit <c>(a ?? b) ?? c</c> reaches this
	/// method with its left operand wrapped in a parenthesized context, and that grouping is the
	/// author's, so it must be preserved. Rotating on the built <c>CoalesceExpressionSyntax</c>
	/// instead would silently rewrite the grouping.
	/// </para>
	/// </remarks>
	private ExpressionSyntax BuildCoalesceExpression(CvoloParser.OptionCoalescingExpressionContext context)
	{
		var leftContext = context.expression(0);
		var left = BuildOptionalOperand(leftContext, context);
		var right = BuildOptionalOperand(context.expression(1), context);

		// Only a chain the grammar nested on its own is rotated; a parenthesized grouping is not.
		if (leftContext is CvoloParser.OptionCoalescingExpressionContext && left is CoalesceExpressionSyntax inner)
		{
			return new CoalesceExpressionSyntax(
				SpanOf(context),
				inner.Left,
				new CoalesceExpressionSyntax(inner.Span, inner.Right, right));
		}

		return new CoalesceExpressionSyntax(SpanOf(context), left, right);
	}

	// A ternary (`x ? y : z`) or option fallback (`x ?? y`) can be typed while the operator's other
	// operands are still missing. ANTLR then reports no ExpressionContext for the not-yet-typed
	// operand, and building it unconditionally used to throw (a NullReferenceException walking a null
	// context), which dropped the whole document's parse unit and wiped its semantic tokens and
	// codelens. Synthesize a zero-length placeholder so the AST stays well-formed and the analyzer
	// recovers until the operand is typed.
	private ExpressionSyntax BuildOptionalOperand(CvoloParser.ExpressionContext? operand, ParserRuleContext owner)
	{
		if (operand is { } ctx)
			return BuildExpression(ctx);

		var end = owner.Stop?.StopIndex ?? owner.Start?.StartIndex ?? -1;
		return new IdentifierExpressionSyntax(new TextSpan(Math.Max(0, end + 1), 0), "");
	}

	private static string DecodeRawString(string text)
	{
		var body = text[2..^1];
		return body.Replace("\"\"", "\"");
	}

	private static AsmOptions ParseAsmOption(CvoloParser.AsmOptionContext ctx)
	{
		if (ctx.VOLATILE() is not null) return AsmOptions.Volatile;
		if (ctx.ALIGNSTACK() is not null) return AsmOptions.AlignStack;
		if (ctx.INTEL() is not null) return AsmOptions.Intel;
		return AsmOptions.None;
	}

	private static (char Value, string? OutOfRangeHex) DecodeCharLiteral(string content)
	{
		if (content.Length == 1)
			return (content[0], null);

		if (content.Length >= 2 && content[0] == '\\')
		{
			return content[1] switch
			{
				'n' => ('\n', null),
				't' => ('\t', null),
				'r' => ('\r', null),
				'\'' => ('\'', null),
				'"' => ('"', null),
				'\\' => ('\\', null),
				'0' => ('\0', null),
				'x' or 'X' => DecodeHexChar(content),
				_ => ('\0', null),
			};
		}

		return ('\0', null);
	}

	private static (char Value, string? OutOfRangeHex) DecodeHexChar(string content)
	{
		var hexPart = content[2..];
		if (int.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
			return hexValue <= 255 ? ((char)hexValue, null) : ('\0', hexPart);

		return ('\0', hexPart);
	}

	private static (ulong Value, string? LiteralType, string? OverflowType) DecodeIntegerLiteral(string text)
	{
		var digits = text.Replace("_", "");
		var numberBase = 10;
		if (digits.Length > 2 && digits[0] == '0' && (digits[1] == 'x' || digits[1] == 'X'))
		{
			numberBase = 16;
			digits = digits[2..];
		}
		else if (digits.Length > 2 && digits[0] == '0' && (digits[1] == 'b' || digits[1] == 'B'))
		{
			numberBase = 2;
			digits = digits[2..];
		}

		string? suffix = null;
		var last = digits[^1];
		if (last is 'u' or 'U' or 'l' or 'L')
		{
			var prev = digits.Length > 1 ? digits[^2] : '\0';
			if (prev is 'u' or 'U' or 'l' or 'L')
			{
				suffix = digits[^2..];
				digits = digits[..^2];
			}
			else
			{
				suffix = digits[^1..];
				digits = digits[..^1];
			}
		}

		ulong value;
		try
		{
			value = numberBase == 10 ? ulong.Parse(digits, CultureInfo.InvariantCulture) : Convert.ToUInt64(digits, numberBase);
		}
		catch (Exception ex) when (ex is OverflowException or FormatException)
		{
			return (0, null, "ulong");
		}

		return suffix switch
		{
			"u" or "U" => value <= uint.MaxValue ? (value, "uint", null) : (value, "uint", "uint"),
			"l" or "L" => value <= long.MaxValue ? (value, "long", null) : (value, "long", "long"),
			"ul" or "uL" or "Ul" or "UL" or "lu" or "lU" or "Lu" or "LU" => (value, "ulong", null),
			_ => value <= int.MaxValue ? (value, null, null)
				: value <= long.MaxValue ? (value, "long", null)
				: (value, "long", null),
		};
	}

	private static (double Value, bool IsFloat) DecodeDoubleLiteral(string text)
	{
		var digits = text.Replace("_", "");
		var isFloat = false;
		var last = digits[^1];
		if (last is 'f' or 'F')
		{
			isFloat = true;
			digits = digits[..^1];
		}
		else if (last is 'd' or 'D')
		{
			digits = digits[..^1];
		}

		return (double.Parse(digits, CultureInfo.InvariantCulture), isFloat);
	}

	private static string GetAlphaTail(string text)
	{
		var index = text.Length - 1;
		while (index >= 0 && char.IsLetter(text[index]))
			index--;
		return text[(index + 1)..];
	}

	private VariableDeclarationSyntax BuildVariableDeclaration(CvoloParser.VariableDeclarationContext context)
	{
		// During error recovery ANTLR can hand back a variableDeclaration whose children are
		// missing (a lone `W` on its own line is re-parsed as `W <next statement>`). The name and
		// expression are therefore guarded; the empty name resolves to no symbol, so the analyzer
		// recovers and the document keeps its semantic tokens and codelens until the edit is fixed.
		var identifierOverride = context.Identifier()?.GetText() ?? "";

		if (context.LPAREN() is not null)
		{
			var isMutable = context.VAR() is not null;
			var typeCtx = context.type();
			var baseTypeName = GetTypeName(typeCtx);
			var name = identifierOverride;

			ExpressionSyntax valueExpr;
			if (context.parenFieldInitializerList() is { } listCtx)
			{
				var initializers = new List<MemberInitializerSyntax>();
				foreach (var memberInit in listCtx.parenFieldInitializer())
				{
					var memberName = memberInit.Identifier().GetText();
					var fieldExpr = BuildExpression(memberInit.expression());
					initializers.Add(new MemberInitializerSyntax(SpanOf(memberInit), memberName, fieldExpr));
				}

				var elementTypeName = typeCtx is CvoloParser.ArrayTypeContext arrCtx ? GetTypeName(arrCtx.type()) : baseTypeName;
				valueExpr = new StructInitializationExpressionSyntax(SpanOf(context), elementTypeName, initializers);
			}
			else
			{
				valueExpr = BuildExpression(context.expression());
			}

			if (typeCtx is CvoloParser.ArrayTypeContext arrayCtx)
			{
				var sizeExpr = BuildExpression(arrayCtx.expression());

				long countVal;
				if (sizeExpr is IntegerLiteralExpressionSyntax countLit)
				{
					countVal = (long)countLit.Value;
				}
				else if (!long.TryParse(arrayCtx.expression().GetText(), NumberStyles.None, CultureInfo.InvariantCulture, out countVal))
				{
					throw new InvalidOperationException($"Array replication size must be a literal integer: '{arrayCtx.expression().GetText()}'.");
				}

				var countExpr = new IntegerLiteralExpressionSyntax(SpanOf(arrayCtx), (ulong)countVal);
				var implicitInitializer = new ArrayReplicationExpressionSyntax(SpanOf(context), valueExpr, countExpr);
				return new VariableDeclarationSyntax(SpanOf(context), isMutable, baseTypeName, name, implicitInitializer,
					isExplicitVal: context.VAL() is not null);
			}
			else
			{
				return new VariableDeclarationSyntax(SpanOf(context), isMutable, baseTypeName, name, valueExpr,
					isExplicitVal: context.VAL() is not null);
			}
		}

		if (context.REFVAR() is not null)
		{
			var refVarName = identifierOverride;
			var refVarExpr = BuildExpression(context.expression());
			return new VariableDeclarationSyntax(SpanOf(context), isMutable: true, "refvar", refVarName, refVarExpr);
		}

		if (context.REF() is not null)
		{
			var refName = identifierOverride;
			var refExpr = BuildExpression(context.expression());
			return new VariableDeclarationSyntax(SpanOf(context), isMutable: false, "ref", refName, refExpr);
		}

		var isVarMutable = context.VAR() is not null;
		var type = context.type() is not null ? GetTypeName(context.type()) : null;
		var standardName = identifierOverride;

		ExpressionSyntax? initializer = null;
		if (context.expression() is { } expr)
			initializer = BuildExpression(expr);

		return new VariableDeclarationSyntax(SpanOf(context), isVarMutable, type, standardName, initializer,
			isExplicitVal: context.VAL() is not null);
	}

	private IfStatementSyntax BuildIfStatement(CvoloParser.IfStatementContext context)
	{
		var condition = BuildExpression(context.expression());
		var thenStmt = BuildStatement(context.statement(0))!;
		ElseClauseSyntax? elseClause = null;
		if (context.ELSE() is not null && context.statement(1) is { } elseBody)
		{
			var elseStmt = BuildStatement(elseBody)!;
			var elseBlock = elseStmt as BlockStatementSyntax ?? new BlockStatementSyntax(SpanOf(elseBody), [elseStmt]);
			elseClause = new ElseClauseSyntax(SpanOf(elseBody), elseBlock);
		}

		return new IfStatementSyntax(SpanOf(context), condition, thenStmt, elseClause);
	}

	private WhileStatementSyntax BuildWhileStatement(CvoloParser.WhileStatementContext context)
	{
		var label = context.Identifier()?.GetText();
		var condition = BuildExpression(context.expression());
		var body = BuildStatement(context.statement())!;
		return new WhileStatementSyntax(SpanOf(context), condition, body, label);
	}

	private ForStatementSyntax BuildForStatement(CvoloParser.ForStatementContext context)
	{
		var label = context.Identifier()?.GetText();
		var init = BuildVariableDeclaration(context.variableDeclaration());
		var condition = BuildExpression(context.expression(0));
		var increment = BuildExpression(context.expression(1));
		var body = BuildStatement(context.statement())!;
		return new ForStatementSyntax(SpanOf(context), init, condition, increment, body, label);
	}

	private ForEachStatementSyntax BuildForEachStatement(CvoloParser.ForEachStatementContext context)
	{
		var hasLabel = context.COLON() is not null;
		var label = hasLabel ? context.Identifier().GetText() : null;
		var binding = context.forEachBinding();

		var hasVar = binding.VAR() is not null;
		var hasRefVar = binding.REFVAR() is not null;
		var typeCtx = binding.type();

		var itemName = binding.Identifier().GetText();

		// 'refvar' cannot be combined with an explicit item type (spec §1). The grammar parses
		// 'refvar int item' through the 'type Identifier' alternative (type = refVarType / readOnlyRef),
		// so a ref-typed type context signals the invalid combination.
		if (typeCtx is CvoloParser.RefVarTypeContext refVarTypeCtx)
		{
			var refTarget = GetTypeName(refVarTypeCtx.type());
			_diagnostics.Report(_compilationContext, SpanOf(context),
				$"The mutable reference binding form 'refvar' cannot be combined with an explicit item type: 'foreach (refvar {refTarget} {itemName} ...)' is not allowed.", DiagnosticIds.TheMutableReferenceBindingFormRefvarCannotBeCombinedWithAnEx);
			return new ForEachStatementSyntax(SpanOf(context), ForEachVariableKind.RefVar, null, itemName,
				BuildExpression(context.expression()), BuildBlockStatement(context.blockStatement()), label);
		}

		if (typeCtx is CvoloParser.ReadOnlyRefTypeContext readOnlyRefCtx)
		{
			var refTarget = GetTypeName(readOnlyRefCtx.type());
			_diagnostics.Report(_compilationContext, SpanOf(context),
				$"The mutable reference binding form 'refvar' cannot be combined with an explicit item type: 'foreach (refvar {refTarget} {itemName} ...)' is not allowed.", DiagnosticIds.TheMutableReferenceBindingFormRefvarCannotBeCombinedWithAnEx);
			return new ForEachStatementSyntax(SpanOf(context), ForEachVariableKind.RefVar, null, itemName,
				BuildExpression(context.expression()), BuildBlockStatement(context.blockStatement()), label);
		}

		var kind = hasRefVar
			? ForEachVariableKind.RefVar
			: hasVar
				? ForEachVariableKind.Var
				: ForEachVariableKind.Val;

		var explicitItemType = typeCtx is not null ? GetTypeName(typeCtx) : null;

		var collection = BuildExpression(context.expression());
		var body = BuildBlockStatement(context.blockStatement());
		return new ForEachStatementSyntax(SpanOf(context), kind, explicitItemType, itemName, collection, body, label);
	}

	private SyntaxNode BuildControlExitStatement(CvoloParser.ControlExitStatementContext context)
	{
		var isBreak = context.BREAK() is not null;
		var label = context.Identifier()?.GetText();
		return isBreak
			? new BreakStatementSyntax(SpanOf(context), label)
			: new ContinueStatementSyntax(SpanOf(context), label);
	}

	private static TextSpan SpanOf(Antlr4.Runtime.ParserRuleContext context)
	{
		if (context is null || context.Start is null)
			return new TextSpan(0, 0);

		// Error recovery can synthesize tokens with StartIndex/StopIndex -1 (a "missing"
		// identifier, an EOF stop). Never let those leak into a span: clamp to the file start.
		var start = Math.Max(context.Start.StartIndex, 0);
		var stop = context.Stop?.StopIndex ?? -1;
		var end = stop >= start ? stop + 1 : start;
		return TextSpan.FromBounds(start, end);
	}

	private static TextSpan SpanOf(Antlr4.Runtime.IToken token)
	{
		if (token is null)
			return new TextSpan(0, 0);

		var start = Math.Max(token.StartIndex, 0);
		var end = token.StopIndex >= start ? token.StopIndex + 1 : start;
		return TextSpan.FromBounds(start, end);
	}

	/// <summary>
	/// Records the structured extent expression behind a static array type name so the binder can
	/// evaluate it with ordinary expression precedence (see <c>ConstantExpressionEvaluator</c>).
	/// The type name is the canonical flattened spelling returned by <see cref="GetTypeName"/>, so
	/// the binder's later <c>ResolveType(name)</c> call finds exactly this expression.
	/// </summary>
	private void RegisterArrayExtent(string typeName, CvoloParser.ExpressionContext extentContext)
	{
		if (_compilationContext is null || _compilationContext.ArrayExtentExpressions.ContainsKey(typeName))
			return;

		try
		{
			_compilationContext.ArrayExtentExpressions[typeName] = BuildExpression(extentContext);
		}
		catch
		{
			// A malformed extent is reported by the normal parse/semantic passes; leaving it
			// unregistered simply keeps the array type unresolved here.
		}
	}

	private string GetTypeName(CvoloParser.TypeContext context)
	{
		if (context is null)
		{
			return string.Empty;
		}

		if (context is CvoloParser.RefVarTypeContext refVarCtx)
		{
			return $"refvar {GetTypeName(refVarCtx.type())}";
		}

		if (context is CvoloParser.ReadOnlyRefTypeContext refCtx)
		{
			return $"ref {GetTypeName(refCtx.type())}";
		}

		if (context is CvoloParser.PointerTypeContext ptrCtx)
		{
			return $"{GetTypeName(ptrCtx.type())}*";
		}

		if (context is CvoloParser.ArrayTypeContext arrCtx)
		{
			var arrayTypeName = $"{GetTypeName(arrCtx.type())}[{arrCtx.expression().GetText()}]";
			RegisterArrayExtent(arrayTypeName, arrCtx.expression());
			return arrayTypeName;
		}

		if (context is CvoloParser.SliceTypeContext sliceCtx)
		{
			return $"{GetTypeName(sliceCtx.type())}[]";
		}

		if (context is CvoloParser.QualifiedTypeContext qualCtx)
		{
			return qualCtx.qualifiedName().GetText();
		}

		if (context is CvoloParser.GenericInstantiationTypeContext genInstCtx)
		{
			// A recovered typeList may be absent or have error nodes / inserted
			// tokens. Never manufacture a normal type spelling from that tree.
			if (genInstCtx.type() is null || genInstCtx.typeList() is not { } arguments
				|| arguments.type().Length == 0 || ContainsRecoveryError(genInstCtx))
				return string.Empty;
			var innerTypeName = GetTypeName(genInstCtx.type());
			var args = arguments.type().Select(GetTypeName).ToArray();
			if (string.IsNullOrWhiteSpace(innerTypeName) || args.Any(string.IsNullOrWhiteSpace))
				return string.Empty;
			return $"{innerTypeName}<{string.Join(", ", args)}>";
		}

		if (context is CvoloParser.OptionalTypeContext optCtx)
		{
			if (optCtx.type() is CvoloParser.OptionalTypeContext)
			{
				_diagnostics.Report(_compilationContext!, SpanOf(context), "Multiple '?' in a row are not allowed. Use Option<Option<T>> for nested optional types.",
					DiagnosticIds.OptionalTypeChainForbidden);
			}

			return $"{GetTypeName(optCtx.type())}?";
		}

		// `T??` in type position. The fallback operator owns a dedicated `??` token, so a chained
		// optional type no longer lexes as two `?` tokens. The optional type chain is still rejected
		// here, with the same diagnostic the parser used before `??` became its own token.
		if (context is CvoloParser.OptionalTypeChainContext chainCtx)
		{
			_diagnostics.Report(_compilationContext!, SpanOf(context), "Multiple '?' in a row are not allowed. Use Option<Option<T>> for nested optional types.",
				DiagnosticIds.OptionalTypeChainForbidden);

			return GetTypeName(chainCtx.type());
		}

		return context.GetText();
	}

	private static bool ContainsRecoveryError(IParseTree? node)
	{
		if (node is null || node is IErrorNode || node is ITerminalNode { Symbol.TokenIndex: < 0 })
			return true;
		for (var i = 0; i < node.ChildCount; i++)
			if (ContainsRecoveryError(node.GetChild(i)))
				return true;
		return false;
	}

	private string GetReturnTypeName(CvoloParser.ReturnTypeContext context)
	{
		if (context.VOID() is not null)
			return "void";
		return GetTypeName(context.type());
	}

	// Turns a lexer "token recognition error" into a CVL1911 diagnostic anchored on the offending
	// character. The default ANTLR listener writes to the console, which the compiler driver never
	// sees and the language server spams to stderr on every reparse.
	private sealed class LexerErrorListener(DiagnosticBag diagnostics, CompilationContext context, List<TextSpan> rejectedImpossibleSpans) : IAntlrErrorListener<int>
	{
		public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
		{
			var source = context.Source;
			var start = e is LexerNoViableAltException lexerError
				? lexerError.StartIndex
				: OffsetOf(source, line, charPositionInLine);

			var length = e is LexerNoViableAltException bad
				? bad.InputStream.Index - bad.StartIndex
				: 1;

			// A lexer fault with coordinates that cannot exist in the source is not silently
			// relocated to offset 0. Prefer the defensible line/column mapping the lexer already
			// gives us; only an unusable mapping is recorded as an internal fault and dropped.
			if (start < 0 || start > source.Length || length < 1 || start + length > source.Length)
			{
				var fallback = OffsetOf(source, line, charPositionInLine);
				if (fallback < 0 || fallback > source.Length)
				{
					rejectedImpossibleSpans.Add(new TextSpan(start, length));
					return;
				}

				start = fallback;
				length = 1;
			}

			if (start + length > source.Length)
				length = source.Length - start;
			if (length < 1)
			{
				diagnostics.Report(context, new TextSpan(start, 0),
					$"({line},{charPositionInLine}): unrecognized character.", DiagnosticIds.UnrecognizedCharacter);
				return;
			}

			var text = source.Substring(start, length);
			diagnostics.Report(context, new TextSpan(start, length),
				$"({line},{charPositionInLine}): unrecognized character '{text}'.", DiagnosticIds.UnrecognizedCharacter);
		}

		private static int OffsetOf(string source, int line, int charPositionInLine)
		{
			var currentLine = 1;
			var index = 0;
			while (index < source.Length && currentLine < line)
			{
				if (source[index] == '\n')
					currentLine++;
				index++;
			}

			var offset = index + charPositionInLine;
			return offset > source.Length ? source.Length : offset;
		}
	}

	private sealed class SyntaxErrorListener(DiagnosticBag diagnostics, CompilationContext context, CommonTokenStream tokenStream, List<TextSpan> rejectedImpossibleSpans) : BaseErrorListener
	{
		// Each anchored fault (a malformed literal ANTLR skips, or the unsupported `??=`)
		// opens a bounded original-source recovery region. Later parser errors are treated
		// as a cascade only when they sit on the same physical line *before* any statement
		// boundary, so an independent fault later on the same line is still reported.
		private readonly List<(int AnchorTokenIndex, int Line)> _recoveryRegions = new();
		private readonly HashSet<(int Position, int Token)> _missingDelimiters = new();
		private int _genericRecoveryStart = -1;
		private int _genericRecoveryEnd = -1;

		public void SuppressGenericRecovery(int start, int end)
		{
			_genericRecoveryStart = start;
			_genericRecoveryEnd = end;
		}


		/// <summary>
		/// Start indices of DOT tokens the parser rejected. Used after parsing to turn a top-level
		/// leading dot into the dedicated associated-function diagnostic.
		/// </summary>
		public HashSet<int> RejectedDotIndices { get; } = new();

		public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
		{
			// A lexical generic-argument diagnostic is already the root cause.
			// Drop only ANTLR's dependent recovery errors inside that construct;
			// later independent declarations keep their own parser diagnostics.
			if (_genericRecoveryStart >= 0 && offendingSymbol is not null
				&& offendingSymbol.StartIndex >= _genericRecoveryStart
				&& offendingSymbol.StartIndex <= _genericRecoveryEnd)
				return;
			if (offendingSymbol is { Type: CvoloLexer.DOT })
				RejectedDotIndices.Add(offendingSymbol.StartIndex);

			var isEof = offendingSymbol is { Type: TokenConstants.EOF };
			var start = offendingSymbol?.StartIndex ?? 0;
			var length = isEof ? 0 : offendingSymbol?.Text?.Length ?? 0;

			// A non-EOF token whose coordinates cannot exist in the source is an internal fault:
			// record it and omit the outward diagnostic rather than silently clamping the span to
			// [0, Length] or relocating it to EOF. EOF and missing-token insertion points are the
			// only mappings allowed to be defensively clamped.
			if (offendingSymbol is not null && !isEof
				&& (start < 0 || length < 0 || start > context.Source.Length || start + length > context.Source.Length))
			{
				rejectedImpossibleSpans.Add(new TextSpan(start, length < 0 ? 0 : length));
				return;
			}

			if (start < 0)
				start = 0;
			if (start > context.Source.Length)
				start = context.Source.Length;
			if (length > context.Source.Length - start)
				length = context.Source.Length - start;
			if (length < 0)
				length = 0;

			var span = new TextSpan(start, length);

			// The lexer emits dedicated `Bad*` tokens for malformed literals. The parser may skip
			// such a token and report the resulting cascade on a later token (e.g. the `:`/`)` in
			// `n(1value: 0)`). Anchor that error on the malformed literal instead and open a
			// bounded recovery region so only the genuine cascade inside the same statement is
			// quieted; an independent error later on the same line still surfaces.
			var anchor = FindPrecedingBadLiteral(offendingSymbol);
			if (anchor is not null)
			{
				span = new TextSpan(anchor.StartIndex, anchor.Text!.Length);
				RecordRecoveryRegion(anchor, line);
			}
			else if (IsInRecoveryRegion(offendingSymbol, line))
			{
				return;
			}

			// `??=` has no dedicated token: maximal munch splits it into `??` and `=`, so the parser
			// only trips on the `=`. Recognising that shape here gives the unsupported fallback
			// assignment its own diagnostic instead of a bare "unexpected token '='".
			if (TryReportCoalesceAssign(offendingSymbol, span, out var coalesceAssignSpan))
			{
				diagnostics.Report(context, coalesceAssignSpan,
					"The fallback assignment operator '??=' is not supported. Write 'target = target ?? value' instead.",
					DiagnosticIds.CoalesceAssignIsNotSupported);
				RecordRecoveryRegion(offendingSymbol!, line);
				return;
			}

			// Use the grammar's active owner and expected-token set, not regexes over
			// ANTLR's human-readable English recovery message. The insertion point
			// belongs to the completed previous token, not the next valid statement.
			if (TryReportMissingDelimiter(recognizer, offendingSymbol))
				return;

			// In a named-field rule the owner determines which separators are legal.
			// Do not treat == or += as a field assignment, and do not relax (X: ...).
			if (TryReportInvalidFieldSeparator(recognizer, offendingSymbol, span))
				return;

			var simplified = SimplifyMessage(offendingSymbol, msg);
			var diagnosticId = offendingSymbol is { Type: TokenConstants.EOF }
				? DiagnosticIds.UnexpectedEndOfFile
				: simplified.StartsWith("unexpected token", StringComparison.Ordinal)
					? DiagnosticIds.UnexpectedToken
					: simplified.StartsWith("missing ", StringComparison.Ordinal)
						? DiagnosticIds.ExpectedToken
						: DiagnosticIds.Diagnostic;
			diagnostics.Report(context, span, $"({line},{charPositionInLine}): {simplified}", diagnosticId);
		}

		/// <summary>
		/// Detects the <c>??=</c> shape: a rejected <c>=</c> immediately preceded by the fallback
		/// operator token. Returns the combined <c>??=</c> span so the highlight covers the whole
		/// unsupported operator rather than just its tail.
		/// </summary>
		private bool TryReportInvalidFieldSeparator(IRecognizer recognizer, IToken? offending, TextSpan span)
		{
			if (recognizer is not Parser parser || offending is null)
				return false;
			var expected = parser.GetExpectedTokens();
			for (var owner = parser.Context; owner is not null; owner = owner.Parent as ParserRuleContext)
			{
				if (owner is CvoloParser.BracedFieldInitializerContext)
				{
					if (!expected.Contains(CvoloLexer.COLON) && !expected.Contains(CvoloLexer.ASSIGN))
						return false;
					diagnostics.Report(context, span, "Expected ':' or '=' after field name.", DiagnosticIds.ExpectedToken);
					return true;
				}
				if (owner is CvoloParser.ParenFieldInitializerContext)
				{
					if (!expected.Contains(CvoloLexer.COLON))
						return false;
					diagnostics.Report(context, span, "Expected ':' after field name.", DiagnosticIds.ExpectedToken);
					return true;
				}
			}
			return false;
		}

		private bool TryReportCoalesceAssign(IToken? offendingSymbol, TextSpan span, out TextSpan coalesceSpan)
		{
			coalesceSpan = span;
			if (offendingSymbol is not { Type: CvoloLexer.ASSIGN } || offendingSymbol.TokenIndex <= 0)
				return false;

			var previous = tokenStream.Get(offendingSymbol.TokenIndex - 1);
			if (previous.Type != CvoloLexer.COALESCE)
				return false;

			coalesceSpan = new TextSpan(previous.StartIndex, previous.Text!.Length + offendingSymbol.Text!.Length);
			return true;
		}

		/// <summary>
		/// Diagnose known omitted closers at their zero-width source insertion boundary.
		/// Only a genuine parser expected token inside its owning construct can trigger
		/// this path. Unrelated malformed statements retain normal ANTLR recovery.
		/// </summary>
		private bool TryReportMissingDelimiter(IRecognizer recognizer, IToken? offending)
		{
			if (recognizer is not Parser parser || offending is null)
				return false;

			var expected = parser.GetExpectedTokens();
			var statementBoundary = IsStatementBoundary(offending.Type);
			for (var rule = parser.Context; rule is not null; rule = rule.Parent as ParserRuleContext)
			{
				if (rule is CvoloParser.StructInitializationExpressionContext initializer)
				{
					var closer = initializer.RBRACE()?.Symbol;
					if ((closer is null || closer.StartIndex < 0)
						&& expected.Contains(CvoloLexer.RBRACE) && statementBoundary)
						return ReportMissingToken(offending, CvoloLexer.RBRACE, "Expected '}' to close struct initializer.");
					break;
				}
			}

			if (!expected.Contains(CvoloLexer.SEMI)
				|| !(statementBoundary || offending.Type == CvoloLexer.RBRACE))
				return false;

			for (var rule = parser.Context; rule is not null; rule = rule.Parent as ParserRuleContext)
			{
				if (rule is CvoloParser.VariableDeclarationContext)
					return ReportMissingToken(offending, CvoloLexer.SEMI, "Expected ';' after variable declaration.");
				if (rule is CvoloParser.ExpressionStatementContext)
					return ReportMissingToken(offending, CvoloLexer.SEMI, "Expected ';' after expression statement.");
			}
			return false;
		}

		private bool ReportMissingToken(IToken offending, int missingToken, string message)
		{
			if (offending.TokenIndex < 0)
				return false;
			IToken? previous = null;
			for (var i = offending.TokenIndex - 1; i >= 0; i--)
			{
				var token = tokenStream.Get(i);
				if (token.Channel != Lexer.DefaultTokenChannel || token.Type == TokenConstants.EOF)
					continue;
				previous = token;
				break;
			}
			if (previous is null)
				return false;

			var at = Math.Clamp(previous.StopIndex + 1, 0, context.Source.Length);
			if (_missingDelimiters.Add((at, missingToken)))
				diagnostics.Report(context, new TextSpan(at, 0), message, DiagnosticIds.ExpectedToken);
			return true;
		}

		private static bool IsStatementBoundary(int token) => token is
			CvoloLexer.RETURN or CvoloLexer.VAL or CvoloLexer.VAR or CvoloLexer.REF or CvoloLexer.REFVAR
			or CvoloLexer.IF or CvoloLexer.WHILE or CvoloLexer.FOR or CvoloLexer.FOREACH
			or CvoloLexer.SWITCH or CvoloLexer.BREAK or CvoloLexer.CONTINUE or CvoloLexer.DEFER
			or TokenConstants.EOF;

		private static string SimplifyMessage(IToken? offendingSymbol, string message)
		{
			var expectingIndex = message.IndexOf(" expecting ", StringComparison.Ordinal);
			if (expectingIndex >= 0)
				message = message[..expectingIndex];

			if (offendingSymbol is { Type: TokenConstants.EOF })
				return "unexpected end of file";

			if (message.StartsWith("extraneous input ", StringComparison.Ordinal)
				|| message.StartsWith("mismatched input ", StringComparison.Ordinal))
			{
				var quoteStart = message.IndexOf('\'');
				var quoteEnd = quoteStart >= 0 ? message.IndexOf('\'', quoteStart + 1) : -1;
				if (quoteStart >= 0 && quoteEnd > quoteStart)
					return $"unexpected token {message[quoteStart..(quoteEnd + 1)]}";
			}

			return message;
		}

		private IToken? FindPrecedingBadLiteral(IToken? offending)
		{
			if (offending is null)
				return null;

			var tokens = tokenStream.GetTokens();
			for (var i = 0; i < tokens.Count; i++)
			{
				if (tokens[i].StartIndex != offending.StartIndex || tokens[i].Type != offending.Type)
					continue;

				for (var j = i - 1; j >= 0; j--)
				{
					var t = tokens[j];
					if (t.Channel != Lexer.DefaultTokenChannel || t.Type == TokenConstants.EOF)
						continue;

					return IsBadLiteralToken(t.Type) ? t : null;
				}

				return null;
			}

			return null;
		}

		private static bool IsBadLiteralToken(int type) => type is
			CvoloLexer.BadIntegerSuffix or CvoloLexer.BadDoubleLiteral or CvoloLexer.BadLeadingUnderscoreNumber or CvoloLexer.TrailingUnderInteger;

		/// <summary>
		/// Opens (or refreshes) the bounded original-source recovery region of an anchored fault.
		/// The anchor token index and physical line are recorded so a later parser error can be
		/// classified as a recovery cascade of this exact fault.
		/// </summary>
		private void RecordRecoveryRegion(IToken anchor, int line)
		{
			for (var i = 0; i < _recoveryRegions.Count; i++)
			{
				if (_recoveryRegions[i].AnchorTokenIndex == anchor.TokenIndex)
					return;
			}

			_recoveryRegions.Add((anchor.TokenIndex, line));
		}

		/// <summary>
		/// True when <paramref name="offending"/> is a recovery cascade of a previously anchored
		/// fault: same physical line, later in the token stream, and no statement boundary
		/// (<c>;</c>, <c>{</c>, <c>}</c>) crossed in between. A statement boundary proves the parser
		/// has finished the damaged construct, so an error past it is an independent fault.
		/// </summary>
		private bool IsInRecoveryRegion(IToken? offending, int line)
		{
			if (offending is null || offending.TokenIndex < 0)
				return false;

			foreach (var (anchorIndex, anchorLine) in _recoveryRegions)
			{
				if (anchorLine != line || offending.TokenIndex <= anchorIndex)
					continue;
				if (!HasStatementBoundaryBetween(anchorIndex, offending.TokenIndex))
					return true;
			}

			return false;
		}

		private bool HasStatementBoundaryBetween(int fromExclusive, int toExclusive)
		{
			for (var i = fromExclusive + 1; i < toExclusive; i++)
			{
				var token = tokenStream.Get(i);
				if (token.Channel != Lexer.DefaultTokenChannel)
					continue;
				if (token.Type is CvoloLexer.SEMI or CvoloLexer.LBRACE or CvoloLexer.RBRACE)
					return true;
			}

			return false;
		}
	}

	private UsingDirectiveSyntax BuildUsingDirective(CvoloParser.UsingDirectiveContext context)
	{
		// 'using Alias = Namespace.Path;' declares a namespace alias; the leading Identifier of that
		// alternative is the alias name and the following qualifiedName is the canonical target.
		// The plain import alternative has no leading Identifier, so the alias stays null there.
		var alias = context.Identifier();

		return new UsingDirectiveSyntax(
			SpanOf(context),
			context.qualifiedName().GetText(),
			context.EXPOSE() != null,
			alias?.GetText(),
			alias is null ? null : SpanOf(alias.Symbol));
	}

	private NamespaceDeclarationSyntax BuildNamespaceDeclaration(CvoloParser.NamespaceDeclarationContext context)
	{
		var name = context.qualifiedName().GetText();
		var usings = new List<UsingDirectiveSyntax>();
		foreach (var u in context.usingDirective())
			usings.Add(BuildUsingDirective(u));

		var members = new List<SyntaxNode>();
		foreach (var decl in context.declaration())
		{
			var node = BuildDeclaration(decl);
			if (node is not null)
				members.Add(node);
		}

		return new NamespaceDeclarationSyntax(SpanOf(context), name, usings, members);
	}

	private ExtensionDeclarationSyntax BuildExtensionDeclaration(CvoloParser.ExtensionDeclarationContext context)
	{
		var extendedTypeName = context.Identifier().GetText();
		var methods = new List<FunctionDeclarationSyntax>();
		var destructorContexts = new List<CvoloParser.DestructorDeclarationContext>();
		var constructorContexts = new List<CvoloParser.ConstructorDeclarationContext>();
		var operators = new List<OperatorDeclarationSyntax>();
		foreach (var memberCtx in context.extensionMember())
		{
			if (memberCtx.extensionFunctionDeclaration() is { } funcCtx)
				methods.Add(BuildExtensionFunctionDeclaration(funcCtx));
			else if (memberCtx.operatorDeclaration() is { } opCtx)
				operators.Add(BuildOperatorDeclaration(opCtx));
			else if (memberCtx.destructorDeclaration() is { } dtorCtx)
				destructorContexts.Add(dtorCtx);
			else if (memberCtx.constructorDeclaration() is { } ctorCtx)
				constructorContexts.Add(ctorCtx);
		}

		var destructors = new List<DestructorDeclarationSyntax>();
		foreach (var dtorCtx in destructorContexts)
		{
			var dtorAttributes = BuildAttributeList(dtorCtx.attributeList());
			var dtorBody = dtorCtx.blockStatement() is { } dtorBlock ? BuildBlockStatement(dtorBlock) : new BlockStatementSyntax(SpanOf(dtorCtx), []);
			var destructor = new DestructorDeclarationSyntax(SpanOf(dtorCtx), dtorCtx.Identifier().GetText(), dtorBody, dtorAttributes, GetVisibilityModifier(dtorCtx.visibilityModifier()));
			if (dtorCtx.BUILTIN() is { } dtorBuiltin)
			{
				destructor.IsBuiltin = true;
				destructor.IsDeclarationOnly = dtorCtx.blockStatement() is null;
				destructor.BuiltinSpan = SpanOf(dtorBuiltin.Symbol);
			}

			destructors.Add(destructor);
		}

		var constructors = new List<ConstructorDeclarationSyntax>();
		foreach (var ctorCtx in constructorContexts)
		{
			var ctorParams = new List<ParameterSyntax>();
			if (ctorCtx.parameterList() is { } paramListCtx)
			{
				foreach (var param in paramListCtx.parameter())
				{
					if (TryGetReceiverContract(param, out _, out _))
					{
						ReportParseError(param, "Receiver parameter ('refvar this' / 'ref this') is only allowed on extension methods.", DiagnosticIds.ReceiverParameterRefvarThisRefThisIsOnlyAllowedOnExtensionMe);
						continue;
					}

					ctorParams.Add(BuildParameter(param));
				}
			}

			var ctorAttributes = BuildAttributeList(ctorCtx.attributeList());
			var structName = ctorCtx.Identifier(0).GetText();

			// Optional delegating initializer ': this(...)' — match the target as an
			// Identifier (there is no lexer keyword for 'this'), validated as "this".
			IReadOnlyList<ExpressionSyntax>? ctorArgs = null;
			TextSpan? ctorInitSpan = null;
			if (ctorCtx.Identifier().Length > 1)
			{
				var targetId = ctorCtx.Identifier(1);
				if (targetId.GetText() == "this")
				{
					var argList = new List<ExpressionSyntax>();
					var argCtx = ctorCtx.argumentList();
					if (argCtx is not null)
					{
						foreach (var arg in argCtx.expression())
							argList.Add(BuildExpression(arg));
					}

					ctorArgs = argList;
					ctorInitSpan = SpanOf(targetId.Symbol);
				}
			}

			constructors.Add(new ConstructorDeclarationSyntax(SpanOf(ctorCtx), structName, ctorParams, ctorCtx.blockStatement() is { } ctorBlock ? BuildBlockStatement(ctorBlock) : new BlockStatementSyntax(SpanOf(ctorCtx), []), ctorArgs, ctorInitSpan, ctorAttributes, GetVisibilityModifier(ctorCtx.visibilityModifier()), nameSpan: SpanOf(ctorCtx.Identifier(0).Symbol)));
			if (ctorCtx.BUILTIN() is { } ctorBuiltin)
			{
				var declarationOnly = ctorCtx.blockStatement() is null;
				var lastConstructor = constructors[^1];
				lastConstructor.IsBuiltin = true;
				lastConstructor.IsDeclarationOnly = declarationOnly;
				lastConstructor.BuiltinSpan = SpanOf(ctorBuiltin.Symbol);
			}
		}

		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		// Deconstruct tuple into defaults and constraints
		var (defaults, constraints) = BuildWhereClauses(context.whereClause());

		string? conformsTo = null;
		if (context.qualifiedName() is { } conformsCtx)
			conformsTo = conformsCtx.GetText();

		return new ExtensionDeclarationSyntax(SpanOf(context), extendedTypeName, methods, destructors, constructors, generics, conformsTo, GetVisibilityModifier(context.visibilityModifier()), defaults, nameSpan: SpanOf(context.Identifier().Symbol), conformsToSpan: context.qualifiedName() is { } conformsSpanCtx ? SpanOf(conformsSpanCtx) : null, operators: operators, genericParameterConstraints: constraints);
	}

	/// <summary>
	/// Builds an operator overload declaration. Operators are receiverless associated callables, so
	/// the shape is fixed: a mandatory plain parameter list (one operand for prefix operators, two
	/// for infix ones), no type parameters and no body-optional receiver. The arity and the
	/// overloadability of the spelling are validated during declaration registration, which owns
	/// the semantic rules; parsing only rejects the parts that cannot be represented at all.
	/// </summary>
	private OperatorDeclarationSyntax BuildOperatorDeclaration(CvoloParser.OperatorDeclarationContext context)
	{
		var operatorCtx = context.overloadableOperator();
		var spelling = operatorCtx.GetText();
		var operatorSpan = SpanOf(operatorCtx);

		var parameters = new List<ParameterSyntax>();
		var hasSyntaxError = false;
		if (context.parameterList() is { } operatorParamList)
		{
			foreach (var param in operatorParamList.parameter())
			{
				if (TryGetReceiverContract(param, out _, out _))
				{
					ReportParseError(param, $"Operator '{spelling}' cannot declare an instance receiver; operators are receiverless.", DiagnosticIds.OperatorWithReceiver);
					hasSyntaxError = true;
					continue;
				}

				parameters.Add(BuildParameter(param));
			}
		}

		// A single operand selects the prefix form of '+' and '-'; every other spelling is infix.
		// The best-effort token keeps the node well-formed for tooling even when the arity is wrong;
		// registration then reports the arity diagnostic against the real parameter list.
		var unary = parameters.Count == 1;
		var token = OperatorTokens.TryGetToken(spelling, unary)
			?? OperatorTokens.TryGetToken(spelling, !unary)
			?? (spelling == "=" ? OperatorTokens.Prefix + "assign" : OperatorTokens.Prefix + "invalid");

		return new OperatorDeclarationSyntax(
			SpanOf(context),
			GetReturnTypeName(context.returnType()),
			spelling,
			token,
			OperatorTokens.IsUnary(token),
			parameters,
			context.blockStatement() is { } opBlock ? BuildBlockStatement(opBlock) : null,
			BuildAttributeList(context.attributeList()),
			GetVisibilityModifier(context.visibilityModifier()),
			operatorSpan,
			SpanOf(context.returnType()))
		{
			HasSyntaxError = hasSyntaxError
		};
	}

	private InterfaceDeclarationSyntax BuildInterfaceDeclaration(CvoloParser.InterfaceDeclarationContext context)
	{
		var name = context.Identifier().GetText();

		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		var members = new List<InterfaceMethodDeclarationSyntax>();
		foreach (var memberCtx in context.interfaceMember())
		{
			var returnType = GetReturnTypeName(memberCtx.returnType());
			var memberName = memberCtx.Identifier().GetText();

			var parameters = new List<ParameterSyntax>();
			if (memberCtx.parameterList() is { } paramListCtx)
			{
				foreach (var param in paramListCtx.parameter())
				{
					if (TryGetReceiverContract(param, out _, out _))
					{
						ReportParseError(param, "Receiver parameter ('refvar this' / 'ref this') is only allowed on extension methods.", DiagnosticIds.ReceiverParameterRefvarThisRefThisIsOnlyAllowedOnExtensionMe);
						continue;
					}

					parameters.Add(BuildParameter(param));
				}
			}

			members.Add(new InterfaceMethodDeclarationSyntax(SpanOf(memberCtx), returnType, memberName, parameters));
		}

		var attributes = BuildAttributeList(context.attributeList());
		var bases = context.qualifiedName().Select(q => q.GetText()).ToList();
		var constraint = context.FOR() is not null && context.type() is { } typeCtx ? GetTypeName(typeCtx) : null;
		return new InterfaceDeclarationSyntax(SpanOf(context), name, generics, members, bases, constraint, attributes, GetVisibilityModifier(context.visibilityModifier()));
	}

	private ProtocolDeclarationSyntax BuildProtocolDeclaration(CvoloParser.ProtocolDeclarationContext context)
	{
		var name = context.Identifier().GetText();

		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		var members = new List<ProtocolMethodDeclarationSyntax>();
		foreach (var memberCtx in context.protocolMember())
		{
			var returnType = GetReturnTypeName(memberCtx.returnType());
			var memberName = memberCtx.Identifier().GetText();

			var parameters = new List<ParameterSyntax>();
			if (memberCtx.parameterList() is { } paramListCtx)
			{
				foreach (var param in paramListCtx.parameter())
				{
					if (TryGetReceiverContract(param, out _, out _))
					{
						ReportParseError(param, "Receiver parameter ('refvar this' / 'ref this') is only allowed on extension methods.", DiagnosticIds.ReceiverParameterRefvarThisRefThisIsOnlyAllowedOnExtensionMe);
						continue;
					}

					parameters.Add(BuildParameter(param));
				}
			}

			members.Add(new ProtocolMethodDeclarationSyntax(SpanOf(memberCtx), returnType, memberName, parameters));
		}

		var attributes = BuildAttributeList(context.attributeList());
		var bases = context.qualifiedName().Select(q => q.GetText()).ToList();
		var constraint = context.FOR() is not null && context.type() is { } typeCtx ? GetTypeName(typeCtx) : null;
		return new ProtocolDeclarationSyntax(SpanOf(context), name, generics, members, bases, constraint, attributes, GetVisibilityModifier(context.visibilityModifier()));
	}

	private UnionDeclarationSyntax BuildUnionDeclaration(CvoloParser.UnionDeclarationContext context)
	{
		var name = context.Identifier().GetText();

		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		// Deconstruct tuple into defaults and constraints
		var (defaults, constraints) = BuildWhereClauses(context.whereClause());

		var fields = new List<UnionFieldSyntax>();
		foreach (var field in context.unionField())
			fields.Add(new UnionFieldSyntax(SpanOf(field), GetTypeName(field.type()), field.Identifier().GetText(), GetVisibilityModifier(field.visibilityModifier())));

		var unionAttributes = BuildAttributeList(context.attributeList());
		var unionDeclaration = new UnionDeclarationSyntax(SpanOf(context), name, generics, fields, unionAttributes, GetVisibilityModifier(context.visibilityModifier()), defaults, context.UNSAFE() is not null, genericParameterConstraints: constraints);
		if (context.BUILTIN() is { } builtinToken)
		{
			unionDeclaration.IsBuiltin = true;
			unionDeclaration.BuiltinSpan = SpanOf(builtinToken.Symbol);
		}

		return unionDeclaration;
	}

	private EnumDeclarationSyntax BuildEnumDeclaration(CvoloParser.EnumDeclarationContext context)
	{
		var name = context.Identifier().GetText();

		var storageType = context.type() is { } storageCtx ? GetTypeName(storageCtx) : null;

		var variants = new List<EnumVariantDeclarationSyntax>();
		foreach (var variant in context.enumVariant())
		{
			ExpressionSyntax? value = null;
			if (variant.expression() is { } exprCtx)
				value = BuildExpression(exprCtx);

			variants.Add(new EnumVariantDeclarationSyntax(SpanOf(variant), variant.Identifier().GetText(), value));
		}

		var enumAttributes = BuildAttributeList(context.attributeList());
		var enumDeclaration = new EnumDeclarationSyntax(SpanOf(context), name, storageType, variants, enumAttributes, GetVisibilityModifier(context.visibilityModifier()));
		if (context.BUILTIN() is { } builtinToken)
		{
			enumDeclaration.IsBuiltin = true;
			enumDeclaration.BuiltinSpan = SpanOf(builtinToken.Symbol);
		}

		return enumDeclaration;
	}

	private SyntaxNode BuildSwitchStatement(CvoloParser.SwitchStatementContext context)
	{
		var expr = BuildExpression(context.expression());
		var cases = new List<SwitchCaseSyntax>();
		foreach (var caseCtx in context.switchCase())
		{
			if (caseCtx.DEFAULT() is not null)
			{
				var body = new List<SyntaxNode>();
				foreach (var stmt in caseCtx.statement())
				{
					var stmtNode = BuildStatement(stmt);
					if (stmtNode is not null)
						body.Add(stmtNode);
				}

				cases.Add(new SwitchCaseSyntax(SpanOf(caseCtx), "", null, isDefault: true, body));
			}
			else
			{
				var patternCtx = caseCtx.pattern();
				var variantName = "";
				string? variableName = null;

				if (patternCtx is CvoloParser.VariantPatternContext varPat)
				{
					variantName = varPat.Identifier(0).GetText();
					variableName = varPat.Identifier(1).GetText();
				}
				else if (patternCtx is CvoloParser.ConstantPatternContext constPat)
				{
					variantName = constPat.Identifier().GetText();
				}

				var body = new List<SyntaxNode>();
				foreach (var stmt in caseCtx.statement())
				{
					var stmtNode = BuildStatement(stmt);
					if (stmtNode is not null)
						body.Add(stmtNode);
				}

				cases.Add(new SwitchCaseSyntax(SpanOf(caseCtx), variantName, variableName, isDefault: false, body));
			}
		}

		return new SwitchStatementSyntax(SpanOf(context), expr, cases);
	}
}
