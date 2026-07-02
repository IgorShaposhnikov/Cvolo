namespace Cvolo.Core;

public abstract class SyntaxNode(TextSpan span)
{
	public abstract SyntaxKind Kind { get; }

	public TextSpan Span { get; } = span;

	public abstract IEnumerable<SyntaxNode> GetChildren();
}
