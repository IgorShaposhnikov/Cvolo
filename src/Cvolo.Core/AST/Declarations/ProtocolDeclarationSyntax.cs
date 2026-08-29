using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// A structural (implicit, duck-typed) protocol contract: a name plus a set of
/// required method signatures. Unlike a nominal interface, a protocol is
/// satisfied by any concrete type that provides the required members — there is
/// no explicit conformance declaration. A value/ref parameter typed as a
/// protocol is lowered to a generic template and monomorphized at each concrete
/// call site (static-only dispatch, no vtable).
/// </summary>
public sealed class ProtocolDeclarationSyntax(
	TextSpan span,
	string name,
	IReadOnlyList<string> genericParameters,
	IReadOnlyList<ProtocolMethodDeclarationSyntax> members,
	IReadOnlyList<string>? bases = null,
	string? constraint = null,
	IReadOnlyList<AttributeSyntax>? attributes = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ProtocolDeclaration;

	public string Name { get; } = name;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<ProtocolMethodDeclarationSyntax> Members { get; } = members;
	/// <summary>
	/// Optional `: Parent, Other` base clauses naming other contracts this protocol
	/// aggregates. All bases must themselves be protocols (interface parents are
	/// not allowed). Conforming types implicitly satisfy the parent capability graph.
	/// </summary>
	public IReadOnlyList<string> Bases { get; } = bases ?? [];
	/// <summary>
	/// Optional `for ...` requires-clause naming the contract a conforming type
	/// must itself satisfy (e.g. `protocol ISortable for IComparable&lt;Self&gt;`).
	/// Null when the clause is absent.
	/// </summary>
	public string? Constraint { get; } = constraint;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];

	public override IEnumerable<SyntaxNode> GetChildren() => Members;
}

/// <summary>
/// A required method signature within a protocol. It carries no body; a
/// conforming type must provide a matching implementation (or inherit a default
/// provided via an extension on the protocol).
/// </summary>
public sealed class ProtocolMethodDeclarationSyntax(
	TextSpan span,
	string returnType,
	string name,
	IReadOnlyList<ParameterSyntax> parameters) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ProtocolMember;

	public string ReturnType { get; } = returnType;
	public string Name { get; } = name;
	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;

	public override IEnumerable<SyntaxNode> GetChildren() => Parameters;
}
