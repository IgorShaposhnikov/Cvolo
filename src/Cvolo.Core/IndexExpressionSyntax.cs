namespace Cvolo.Core;

public sealed class IndexExpressionSyntax(TextSpan span, ExpressionSyntax left, ExpressionSyntax index) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.IndexExpression;
	public ExpressionSyntax Left { get; } = left;
	public ExpressionSyntax Index { get; } = index;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Left;
		yield return Index;
	}
}
