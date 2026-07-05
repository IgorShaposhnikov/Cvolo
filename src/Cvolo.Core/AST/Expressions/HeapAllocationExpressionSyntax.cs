using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class HeapAllocationExpressionSyntax(TextSpan span, ExpressionSyntax expression) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.HeapAllocationExpression;
	public ExpressionSyntax Expression { get; } = expression;

	public override IEnumerable<SyntaxNode> GetChildren() { yield return Expression; }
}
