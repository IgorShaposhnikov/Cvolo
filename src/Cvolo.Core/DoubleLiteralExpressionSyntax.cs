namespace Cvolo.Core;

public sealed class DoubleLiteralExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.DoubleLiteralExpression;

    public double Value { get; }

    public DoubleLiteralExpressionSyntax(TextSpan span, double value) : base(span)
    {
        Value = value;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => [];
}
