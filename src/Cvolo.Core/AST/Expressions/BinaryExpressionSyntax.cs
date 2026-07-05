using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class BinaryExpressionSyntax(TextSpan span, ExpressionSyntax left, string op, ExpressionSyntax right) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.BinaryExpression;

	public ExpressionSyntax Left { get; } = left;
	public string Operator { get; } = op;
	public ExpressionSyntax Right { get; } = right;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Left;
		yield return Right;
	}
}
