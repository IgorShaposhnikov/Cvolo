namespace Cvolo.Core;

public sealed class IfStatementSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.IfStatement;

    public ExpressionSyntax Condition { get; }
    public SyntaxNode ThenStatement { get; }
    public ElseClauseSyntax? ElseClause { get; }

    public IfStatementSyntax(
        TextSpan span,
        ExpressionSyntax condition,
        SyntaxNode thenStatement,
        ElseClauseSyntax? elseClause) : base(span)
    {
        Condition = condition;
        ThenStatement = thenStatement;
        ElseClause = elseClause;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Condition;
        yield return ThenStatement;
        if (ElseClause is not null)
            yield return ElseClause;
    }
}

public sealed class ElseClauseSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.BlockStatement;

    public BlockStatementSyntax Body { get; }

    public ElseClauseSyntax(TextSpan span, BlockStatementSyntax body) : base(span)
    {
        Body = body;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Body;
    }
}
