using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// A nominal interface contract: a name plus a set of required method
/// signatures. In the static-only model an interface carries no value
/// representation; a value/ref parameter typed as an interface is lowered to
/// a generic template and monomorphized at each concrete conforming call site.
/// </summary>
public sealed class InterfaceDeclarationSyntax(
	TextSpan span,
	string name,
	IReadOnlyList<string> genericParameters,
	IReadOnlyList<InterfaceMethodDeclarationSyntax> members,
	IReadOnlyList<AttributeSyntax>? attributes = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.InterfaceDeclaration;

	public string Name { get; } = name;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<InterfaceMethodDeclarationSyntax> Members { get; } = members;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];

	public override IEnumerable<SyntaxNode> GetChildren() => Members;
}

/// <summary>
/// A required method signature within an interface. It carries no body; a
/// conforming extension must provide a matching implementation.
/// </summary>
public sealed class InterfaceMethodDeclarationSyntax(
	TextSpan span,
	string returnType,
	string name,
	IReadOnlyList<ParameterSyntax> parameters) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.InterfaceMember;

	public string ReturnType { get; } = returnType;
	public string Name { get; } = name;
	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;

	public override IEnumerable<SyntaxNode> GetChildren() => Parameters;
}
