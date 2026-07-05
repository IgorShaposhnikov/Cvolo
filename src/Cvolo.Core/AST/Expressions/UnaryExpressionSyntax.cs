using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class UnaryExpressionSyntax(TextSpan span, string op, ExpressionSyntax operand) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.UnaryExpression;

	public string Operator { get; } = op;
	public ExpressionSyntax Operand { get; } = operand;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Operand;
	}
}
