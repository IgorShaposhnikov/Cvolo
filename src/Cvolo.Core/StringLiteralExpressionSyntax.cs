namespace Cvolo.Core;

public sealed class StringLiteralExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.StringLiteralExpression;

    public string Value { get; }

    public StringLiteralExpressionSyntax(TextSpan span, string value) : base(span)
    {
        Value = value;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => [];
}
