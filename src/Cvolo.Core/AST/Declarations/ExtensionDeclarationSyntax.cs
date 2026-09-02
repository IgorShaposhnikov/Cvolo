using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class ExtensionDeclarationSyntax(
	TextSpan span,
	string extendedTypeName,
	IReadOnlyList<FunctionDeclarationSyntax> methods,
	IReadOnlyList<DestructorDeclarationSyntax>? destructors = null,
	IReadOnlyList<ConstructorDeclarationSyntax>? constructors = null,
	IReadOnlyList<string>? genericParameters = null,
	string? conformsTo = null,
	Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExtensionDeclaration;

	public string ExtendedTypeName { get; } = extendedTypeName;
	public IReadOnlyList<FunctionDeclarationSyntax> Methods { get; } = methods;
	public IReadOnlyList<DestructorDeclarationSyntax> Destructors { get; } = destructors ?? [];
	public IReadOnlyList<ConstructorDeclarationSyntax> Constructors { get; } = constructors ?? [];
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters ?? [];

	/// <summary>The nominal interface this extension makes its type conform to, if any.</summary>
	public string? ConformsTo { get; } = conformsTo;

	/// <summary>Baseline visibility inherited by all members of this block; null means unspecified (defaults to internal).</summary>
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public override IEnumerable<SyntaxNode> GetChildren() => [.. Methods, .. Destructors, .. Constructors];
}
