namespace Cvolo.Core;

public sealed class IdentifierExpressionSyntax(TextSpan span, string name) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.IdentifierExpression;

	public string Name { get; } = name;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
