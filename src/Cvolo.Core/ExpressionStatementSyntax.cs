namespace Cvolo.Core;

public sealed class ExpressionStatementSyntax(TextSpan span, ExpressionSyntax expression) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExpressionStatement;

	public ExpressionSyntax Expression { get; } = expression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Expression;
	}
}
