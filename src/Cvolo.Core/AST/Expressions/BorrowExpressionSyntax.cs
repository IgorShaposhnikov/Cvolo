using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class BorrowExpressionSyntax(TextSpan span, ExpressionSyntax expression) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.BorrowExpression;
	public ExpressionSyntax Expression { get; } = expression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Expression;
	}
}
