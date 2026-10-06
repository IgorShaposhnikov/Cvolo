using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// One element of an interpolated string: either literal text or a parsed expression segment.
/// </summary>
/// <param name="Text">The raw segment text (for literal segments) or the expression source.</param>
/// <param name="IsExpression">True when <paramref name="Expression"/> carries a parsed expression.</param>
/// <param name="Expression">The parsed expression for expression segments, or null for literal text.</param>
public sealed record InterpolatedStringSegment(string Text, bool IsExpression, ExpressionSyntax? Expression);

public sealed class InterpolatedStringExpressionSyntax(TextSpan span, string rawText) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.InterpolatedStringExpression;

	public string RawText { get; } = rawText;

	/// <summary>
	/// Segments produced by the syntax-phase interpolation rewriter. The rewriter runs before semantic
	/// analysis, so the binder can consume pre-parsed expression segments without re-parsing. Empty
	/// when the node was never processed by the rewriter.
	/// </summary>
	public List<InterpolatedStringSegment> Segments { get; } = [];

	public override IEnumerable<SyntaxNode> GetChildren() => Enumerable.Empty<SyntaxNode>();
}
