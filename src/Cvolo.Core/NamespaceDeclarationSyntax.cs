namespace Cvolo.Core;

public sealed class NamespaceDeclarationSyntax(
	TextSpan span,
	string name,
	IReadOnlyList<UsingDirectiveSyntax> usings,
	IReadOnlyList<SyntaxNode> members) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.NamespaceDeclaration;
	public string Name { get; } = name;
	public IReadOnlyList<UsingDirectiveSyntax> Usings { get; } = usings;
	public IReadOnlyList<SyntaxNode> Members { get; } = members;

	public override IEnumerable<SyntaxNode> GetChildren() => [.. Usings, .. Members];
}
