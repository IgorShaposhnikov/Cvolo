namespace Cvolo.Core;

public sealed class ArrayInitializationExpressionSyntax(TextSpan span, IReadOnlyList<ExpressionSyntax> elements) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.ArrayInitializationExpression;
	public IReadOnlyList<ExpressionSyntax> Elements { get; } = elements;

	public override IEnumerable<SyntaxNode> GetChildren() => Elements;
}
