using Antlr4.Runtime;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Statements;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Computes syntax-aware folding regions for a document. The regions come from the parsed syntax
/// tree and the comment tokens, never from a brace scan of the raw text, so an incomplete or
/// malformed document still yields the regions that the parser recovered confidently.
/// </summary>
internal static class FoldingRangeService
{
	/// <summary>
	/// Returns every foldable region in <paramref name="document"/>, ordered by start position and
	/// then by decreasing length so an enclosing region is never hidden behind an inner one.
	/// </summary>
	public static IReadOnlyList<ToolingFoldingRange> GetFoldingRanges(ProjectSnapshot snapshot, DocumentSnapshot document)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(document);

		var text = document.Text;
		var source = text.ToString();
		var ranges = new List<ToolingFoldingRange>();
		var seen = new HashSet<TextSpan>();

		if (snapshot.TryGetParsedUnit(document.Id, out var unit) && unit is not null)
			AddSyntaxRegions(unit, source, text, ranges, seen);

		AddCommentRegions(source, text, ranges, seen);

		return
		[
			.. ranges
				.OrderBy(range => range.Range.Start)
				.ThenByDescending(range => range.Range.Length)
		];
	}

	private static void AddSyntaxRegions(SyntaxNode node, string source, SourceText text, List<ToolingFoldingRange> ranges, HashSet<TextSpan> seen)
	{
		AddRegion(node, source, text, ranges, seen);

		foreach (var child in node.GetChildren())
			AddSyntaxRegions(child, source, text, ranges, seen);
	}

	private static void AddRegion(SyntaxNode node, string source, SourceText text, List<ToolingFoldingRange> ranges, HashSet<TextSpan> seen)
	{
		switch (node)
		{
			// Aggregates and callables all present their body as the first '{' and the last '}' inside
			// the declaration itself, so the foldable region is that brace pair without the braces.
			case NamespaceDeclarationSyntax:
			case StructDeclarationSyntax:
			case UnionDeclarationSyntax:
			case EnumDeclarationSyntax:
			case ExtensionDeclarationSyntax:
			case InterfaceDeclarationSyntax:
			case ProtocolDeclarationSyntax:
			case ExternBlockSyntax:
			case ExposeExternBlockSyntax:
			case FunctionDeclarationSyntax:
			case ConstructorDeclarationSyntax:
			case DestructorDeclarationSyntax:
			case OperatorDeclarationSyntax:
			case BlockStatementSyntax:
				AddBracedRegion(node.Span, source, text, ranges, seen);
				break;

			// A switch has no single body block, so the region spans its first case through its last.
			case SwitchStatementSyntax switchStatement when switchStatement.Cases.Count > 0:
			{
				var first = switchStatement.Cases[0].Span;
				var last = switchStatement.Cases[^1].Span;
				AddRegion(new TextSpan(first.Start, last.Start + last.Length - first.Start), source, text, ranges, seen);
				break;
			}

			case WhileStatementSyntax whileStatement:
				AddBracedRegion(whileStatement.Body.Span, source, text, ranges, seen);
				break;

			case ForStatementSyntax forStatement:
				AddBracedRegion(forStatement.Body.Span, source, text, ranges, seen);
				break;

			case ForEachStatementSyntax forEachStatement:
				AddBracedRegion(forEachStatement.Body.Span, source, text, ranges, seen);
				break;

			case UnsafeBlockStatementSyntax unsafeBlock:
				AddBracedRegion(unsafeBlock.Body.Span, source, text, ranges, seen);
				break;

			case LabeledBlockStatementSyntax labeledBlock:
				AddBracedRegion(labeledBlock.Body.Span, source, text, ranges, seen);
				break;

			// Each arm folds independently so a long catch or finally body is not dragged along with
			// the whole try statement.
			case TryStatementSyntax tryStatement:
				AddBracedRegion(tryStatement.Body.Span, source, text, ranges, seen);

				foreach (var catchClause in tryStatement.CatchClauses)
					AddBracedRegion(catchClause.Body.Span, source, text, ranges, seen);

				if (tryStatement.FinallyBody is { } finallyBody)
					AddBracedRegion(finallyBody.Span, source, text, ranges, seen);

				break;
		}
	}

	/// <summary>
	/// Turns the first '{' and the last '}' inside <paramref name="span"/> into a foldable region that
	/// covers only what lies between them, so a client folding the region keeps both braces visible.
	/// A recovered node whose span no longer lines up with the captured text simply yields no region.
	/// </summary>
	private static void AddBracedRegion(Cvolo.Core.Diagnostics.TextSpan span, string source, SourceText text, List<ToolingFoldingRange> ranges, HashSet<TextSpan> seen)
	{
		if (span.Start < 0 || span.Start > source.Length)
			return;

		var length = Math.Min(span.Length, source.Length - span.Start);

		if (length <= 0)
			return;

		var open = source.IndexOf('{', span.Start, length);

		if (open < 0)
			return;

		// LastIndexOf with an explicit count rejects a count of zero, so the search walks backwards
		// from the end of the node's own text instead.
		var close = source.LastIndexOf('}', span.Start + length - 1);

		if (close <= open)
			return;

		AddRegion(new TextSpan(open + 1, close - open - 1), source, text, ranges, seen);
	}

	private static void AddRegion(TextSpan region, string source, SourceText text, List<ToolingFoldingRange> ranges, HashSet<TextSpan> seen, ToolingFoldingKind kind = ToolingFoldingKind.None)
	{
		if (region.Length <= 0)
			return;

		if (region.Start < 0 || region.Start + region.Length > text.Length)
			return;

		// A single-line region cannot be folded, and the client would reject a range whose end line
		// precedes its start line.
		if (text.GetLinePosition(region.Start).Line == text.GetLinePosition(region.Start + region.Length).Line)
			return;

		if (seen.Add(region))
			ranges.Add(new ToolingFoldingRange(region, kind));
	}

	/// <summary>
	/// Folds multiline block comments and runs of line comments. A run whose members are all
	/// documentation comments is reported as a documentation region so a client can style it apart.
	/// </summary>
	private static void AddCommentRegions(string source, SourceText text, List<ToolingFoldingRange> ranges, HashSet<TextSpan> seen)
	{
		var lexer = new CvoloLexer(new AntlrInputStream(source));
		var comments = lexer.GetAllTokens()
			.Where(token => token.Channel == CvoloLexer.COMMENTS)
			.ToArray();

		List<IToken> run = [];

		foreach (var token in comments)
		{
			if (token.Type == CvoloLexer.BlockComment)
			{
				FlushRun(run, source, text, ranges, seen);
				run.Clear();
				AddRegion(new TextSpan(token.StartIndex, (token.StopIndex - token.StartIndex) + 1), source, text, ranges, seen, ToolingFoldingKind.Comment);
				continue;
			}

			if (token.Type != CvoloLexer.LineComment)
				continue;

			// A run continues while the previous line comment ended on the line the next one starts
			// on, so blank lines and interleaved declarations start a new region.
			if (run.Count > 0)
			{
				var previous = run[^1];
				var between = source.AsSpan((previous.StopIndex + 1), token.StartIndex - previous.StopIndex - 1);
				var newlines = 0;

				for (var i = 0; i < between.Length && newlines <= 1; i++)
				{
					if (between[i] == '\n')
						newlines++;
				}

				if (newlines > 1)
				{
					FlushRun(run, source, text, ranges, seen);
					run.Clear();
				}
			}

			run.Add(token);
		}

		FlushRun(run, source, text, ranges, seen);
	}

	private static void FlushRun(List<IToken> run, string source, SourceText text, List<ToolingFoldingRange> ranges, HashSet<TextSpan> seen)
	{
		if (run.Count == 0)
			return;

		var first = run[0];
		var last = run[^1];
		var documentation = run.All(token => token.Text.StartsWith("///", StringComparison.Ordinal));
		var kind = documentation ? ToolingFoldingKind.Documentation : ToolingFoldingKind.Comment;

		AddRegion(new TextSpan(first.StartIndex, (last.StopIndex - first.StartIndex) + 1), source, text, ranges, seen, kind);
	}
}
