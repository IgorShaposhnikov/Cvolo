namespace Cvolo.Core;

public sealed class MemberAccessExpressionSyntax(TextSpan span, ExpressionSyntax expression, string memberName) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.MemberAccessExpression;

	public ExpressionSyntax Expression { get; } = expression;
	public string MemberName { get; } = memberName;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Expression;
	}
}
