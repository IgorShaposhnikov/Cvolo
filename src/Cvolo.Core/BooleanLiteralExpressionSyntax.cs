namespace Cvolo.Core;

public sealed class BooleanLiteralExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.BooleanLiteralExpression;

    public bool Value { get; }

    public BooleanLiteralExpressionSyntax(TextSpan span, bool value) : base(span)
    {
        Value = value;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => [];
}
