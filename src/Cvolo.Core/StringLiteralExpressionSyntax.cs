namespace Cvolo.Core;

public sealed class StringLiteralExpressionSyntax(TextSpan span, string value) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.StringLiteralExpression;

	public string Value { get; } = value;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
