namespace Cvolo.Core;

public sealed class BlockStatementSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.BlockStatement;

    public IReadOnlyList<SyntaxNode> Statements { get; }

    public BlockStatementSyntax(TextSpan span, IReadOnlyList<SyntaxNode> statements) : base(span)
    {
        Statements = statements;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Statements;
}
