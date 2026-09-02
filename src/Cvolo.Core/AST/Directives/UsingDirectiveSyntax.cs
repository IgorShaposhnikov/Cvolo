using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Directives;

public sealed class UsingDirectiveSyntax(TextSpan span, string namespaceName, bool isExposed = false) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.UsingDirective;
	public string NamespaceName { get; } = namespaceName;
	public bool IsExposed { get; } = isExposed;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
