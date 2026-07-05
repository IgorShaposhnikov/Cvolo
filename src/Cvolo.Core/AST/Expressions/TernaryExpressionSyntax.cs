using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class TernaryExpressionSyntax(
	TextSpan span,
	ExpressionSyntax condition,
	ExpressionSyntax thenExpression,
	ExpressionSyntax elseExpression) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.TernaryExpression;
	public ExpressionSyntax Condition { get; } = condition;
	public ExpressionSyntax ThenExpression { get; } = thenExpression;
	public ExpressionSyntax ElseExpression { get; } = elseExpression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Condition;
		yield return ThenExpression;
		yield return ElseExpression;
	}
}
