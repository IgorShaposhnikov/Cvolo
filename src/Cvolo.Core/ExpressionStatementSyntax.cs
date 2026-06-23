namespace Cvolo.Core;

public sealed class ExpressionStatementSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.ExpressionStatement;

    public ExpressionSyntax Expression { get; }

    public ExpressionStatementSyntax(TextSpan span, ExpressionSyntax expression) : base(span)
    {
        Expression = expression;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Expression;
    }
}
