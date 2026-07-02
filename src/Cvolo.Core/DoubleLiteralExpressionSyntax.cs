namespace Cvolo.Core;

public sealed class DoubleLiteralExpressionSyntax(TextSpan span, double value) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.DoubleLiteralExpression;

	public double Value { get; } = value;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
