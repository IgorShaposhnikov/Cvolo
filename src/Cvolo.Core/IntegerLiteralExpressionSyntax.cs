namespace Cvolo.Core;

public sealed class IntegerLiteralExpressionSyntax(TextSpan span, int value) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.IntegerLiteralExpression;

	public int Value { get; } = value;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
