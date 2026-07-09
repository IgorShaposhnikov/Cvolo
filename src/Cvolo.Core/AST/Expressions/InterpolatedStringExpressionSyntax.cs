using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class InterpolatedStringExpressionSyntax(TextSpan span, string rawText) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.InterpolatedStringExpression;

	public string RawText { get; } = rawText;

	public override IEnumerable<SyntaxNode> GetChildren() => Enumerable.Empty<SyntaxNode>();
}
