using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Symbols.Structs;

/// <summary>
/// A nominal interface type. In the static-only model an interface has no
/// value representation and is never allocated; it exists so an interface-name
/// annotation (e.g. a value/ref parameter typed as an interface) can be
/// recognized and lowered to the concrete conforming type at each call site.
/// </summary>
/// <remarks>
/// A closed specialization keeps its declaration identity and ordered canonical
/// arguments. <see cref="Name"/> is the canonical key used by conformance evidence,
/// so `IBook&lt;int&gt;` and `IBook&lt;long&gt;` are distinct even when their member
/// shapes happen to be identical.
/// </remarks>
public sealed class InterfaceTypeSymbol(
	string name,
	InterfaceDeclarationSyntax? declaration = null,
	IReadOnlyList<TypeSymbol>? typeArguments = null,
	string? templateName = null) : TypeSymbol(name)
{
	/// <summary>The open declaration this symbol specializes; null for a synthesized marker.</summary>
	public InterfaceDeclarationSyntax? Declaration { get; } = declaration;

	/// <summary>Ordered canonical arguments for a closed interface, empty for an open template.</summary>
	public IReadOnlyList<TypeSymbol> TypeArguments { get; } = typeArguments ?? [];

	/// <summary>Name of the open declaration, used to find its template and base edges.</summary>
	public string TemplateName { get; } = templateName ?? name;

	/// <summary>Whether all declared parameters are bound, including a non-generic interface.</summary>
	public bool IsClosed => Declaration is not null && Declaration.GenericParameters.Count == TypeArguments.Count;
}
