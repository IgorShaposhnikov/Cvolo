namespace Cvolo.Core;

public sealed class IdentifierExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.IdentifierExpression;

    public string Name { get; }

    public IdentifierExpressionSyntax(TextSpan span, string name) : base(span)
    {
        Name = name;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => [];
}
