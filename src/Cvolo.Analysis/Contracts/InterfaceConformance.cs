using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Contracts;

/// <summary>
/// Answers nominal interface-conformance queries against the binding context's registered
/// conformance relationships.
/// </summary>
/// <remarks>
/// Interface conformance remains nominal: embedding or structural shape does not add membership.
/// This service only centralizes the existing query and does not change registration semantics.
/// </remarks>
internal sealed class InterfaceConformance(BindingContext context)
{
	/// <summary>
	/// Returns whether the supplied concrete type explicitly conforms to the requested interface.
	/// Pointer arguments are unwrapped before the nominal conformance table is queried.
	/// </summary>
	public bool Conforms(TypeSymbol type, InterfaceTypeSymbol interfaceType)
	{
		var baseType = type is PointerTypeSymbol pointer ? pointer.ReferencedType : type;
		return context.Conformance.TryGetValue(baseType.Name, out var interfaces)
			&& interfaces.Contains(interfaceType.Name);
	}
}
