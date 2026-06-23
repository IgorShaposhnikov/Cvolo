namespace Cvolo.Core;

public sealed class BinaryExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.BinaryExpression;

    public ExpressionSyntax Left { get; }
    public string Operator { get; }
    public ExpressionSyntax Right { get; }

    public BinaryExpressionSyntax(TextSpan span, ExpressionSyntax left, string op, ExpressionSyntax right) : base(span)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Left;
        yield return Right;
    }
}
