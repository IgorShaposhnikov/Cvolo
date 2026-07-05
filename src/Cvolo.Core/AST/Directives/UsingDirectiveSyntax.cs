using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Directives;

public sealed class UsingDirectiveSyntax(TextSpan span, string namespaceName) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.UsingDirective;
	public string NamespaceName { get; } = namespaceName;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
