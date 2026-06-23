namespace Cvolo.Core;

public sealed class ReturnStatementSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.ReturnStatement;

    public ExpressionSyntax? Expression { get; }

    public ReturnStatementSyntax(TextSpan span, ExpressionSyntax? expression) : base(span)
    {
        Expression = expression;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        if (Expression is not null) yield return Expression;
    }
}
