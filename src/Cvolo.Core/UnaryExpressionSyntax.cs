namespace Cvolo.Core;

public sealed class UnaryExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.UnaryExpression;

    public string Operator { get; }
    public ExpressionSyntax Operand { get; }

    public UnaryExpressionSyntax(TextSpan span, string op, ExpressionSyntax operand) : base(span)
    {
        Operator = op;
        Operand = operand;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Operand;
    }
}
