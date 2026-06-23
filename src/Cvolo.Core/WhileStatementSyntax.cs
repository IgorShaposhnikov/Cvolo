namespace Cvolo.Core;

public sealed class WhileStatementSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.WhileStatement;

    public ExpressionSyntax Condition { get; }
    public SyntaxNode Body { get; }

    public WhileStatementSyntax(TextSpan span, ExpressionSyntax condition, SyntaxNode body) : base(span)
    {
        Condition = condition;
        Body = body;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Condition;
        yield return Body;
    }
}
