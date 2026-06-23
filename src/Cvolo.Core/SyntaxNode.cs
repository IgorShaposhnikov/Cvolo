namespace Cvolo.Core;

public abstract class SyntaxNode
{
    public abstract SyntaxKind Kind { get; }

    public TextSpan Span { get; }

    protected SyntaxNode(TextSpan span)
    {
        Span = span;
    }

    public abstract IEnumerable<SyntaxNode> GetChildren();
}
