using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class ExtensionDeclarationSyntax(
	TextSpan span,
	string extendedTypeName,
	IReadOnlyList<FunctionDeclarationSyntax> methods,
	IReadOnlyList<DestructorDeclarationSyntax>? destructors = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExtensionDeclaration;

	public string ExtendedTypeName { get; } = extendedTypeName;
	public IReadOnlyList<FunctionDeclarationSyntax> Methods { get; } = methods;
	public IReadOnlyList<DestructorDeclarationSyntax> Destructors { get; } = destructors ?? [];

	public override IEnumerable<SyntaxNode> GetChildren() => [.. Methods, .. Destructors];
}
