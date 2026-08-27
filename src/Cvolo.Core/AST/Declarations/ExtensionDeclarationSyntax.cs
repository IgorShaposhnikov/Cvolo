using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class ExtensionDeclarationSyntax(
	TextSpan span,
	string extendedTypeName,
	IReadOnlyList<FunctionDeclarationSyntax> methods,
	IReadOnlyList<DestructorDeclarationSyntax>? destructors = null,
	IReadOnlyList<ConstructorDeclarationSyntax>? constructors = null,
	IReadOnlyList<string>? genericParameters = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExtensionDeclaration;

	public string ExtendedTypeName { get; } = extendedTypeName;
	public IReadOnlyList<FunctionDeclarationSyntax> Methods { get; } = methods;
	public IReadOnlyList<DestructorDeclarationSyntax> Destructors { get; } = destructors ?? [];
	public IReadOnlyList<ConstructorDeclarationSyntax> Constructors { get; } = constructors ?? [];
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters ?? [];

	public override IEnumerable<SyntaxNode> GetChildren() => [.. Methods, .. Destructors, .. Constructors];
}
