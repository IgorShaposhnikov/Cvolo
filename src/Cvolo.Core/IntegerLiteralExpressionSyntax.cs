namespace Cvolo.Core;

public sealed class IntegerLiteralExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.IntegerLiteralExpression;

    public int Value { get; }

    public IntegerLiteralExpressionSyntax(TextSpan span, int value) : base(span)
    {
        Value = value;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => [];
}
