using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class MemberAccessExpressionSyntax(TextSpan span, ExpressionSyntax expression, string memberName, string op = ".") : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.MemberAccessExpression;

	public ExpressionSyntax Expression { get; } = expression;
	public string MemberName { get; } = memberName;
	public string Operator { get; } = op;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Expression;
	}
}
