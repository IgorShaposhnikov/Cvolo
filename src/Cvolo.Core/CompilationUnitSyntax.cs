namespace Cvolo.Core;

public sealed class CompilationUnitSyntax(
	TextSpan span,
	IReadOnlyList<UsingDirectiveSyntax> usings,
	NamespaceDeclarationSyntax? namespaceDeclaration,
	IReadOnlyList<SyntaxNode> members) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.CompilationUnit;
	public IReadOnlyList<UsingDirectiveSyntax> Usings { get; } = usings;
	public NamespaceDeclarationSyntax? NamespaceDeclaration { get; } = namespaceDeclaration;
	public IReadOnlyList<SyntaxNode> Members { get; } = members;

	public override IEnumerable<SyntaxNode> GetChildren() =>
		[.. Usings, .. (NamespaceDeclaration != null ? [NamespaceDeclaration] : Members)];
}
