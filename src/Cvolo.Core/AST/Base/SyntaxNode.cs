using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Base;

public abstract class SyntaxNode(TextSpan span)
{
	public abstract SyntaxKind Kind { get; }

	public TextSpan Span { get; set; } = span;

	public abstract IEnumerable<SyntaxNode> GetChildren();
}
