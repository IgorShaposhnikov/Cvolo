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
	IReadOnlyList<string>? bases = null,
	string? constraint = null,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.InterfaceDeclaration;

	public string Name { get; } = name;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<InterfaceMethodDeclarationSyntax> Members { get; } = members;
	/// <summary>
	/// Optional `: Parent, Other` base clauses naming other contracts this interface
	/// aggregates (interfaces and/or protocols). A conforming type must provide
	/// every member of the transitive closure.
	/// </summary>
	public IReadOnlyList<string> Bases { get; } = bases ?? [];
	/// <summary>
	/// Optional `for ...` requires-clause naming the contract a conforming type
	/// must itself satisfy (e.g. `interface IButton for IWidget&lt;Self&gt;`).
	/// Null when the clause is absent.
	/// </summary>
	public string? Constraint { get; } = constraint;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

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
