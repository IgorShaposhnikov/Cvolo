using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Contracts;

/// <summary>
/// The compiler's own contract conformance semantics, exposed for tooling. A type conforms to
/// an interface nominally and to a protocol structurally; neither relation can be re-derived from
/// the shape of a declaration, so callers ask here instead of matching member names by hand.
/// <para>
/// Conformance depends on the extension methods visible from a compilation unit, so a caller must
/// set <see cref="BindingContext.CurrentUnit"/> (and <see cref="BindingContext.CurrentNamespace"/>)
/// before each query to the unit that should see the candidate methods.
/// </para>
/// </summary>
public sealed class ContractService
{
	private readonly InterfaceConformance _interfaces;
	private readonly ProtocolConformance _protocols;

	public ContractService(BindingContext context)
	{
		_interfaces = new InterfaceConformance(context);
		_protocols = new ProtocolConformance(context, _interfaces);
	}

	/// <summary>Whether a concrete type satisfies a nominal interface.</summary>
	public bool ConformsToInterface(TypeSymbol type, InterfaceTypeSymbol interfaceType)
		=> _interfaces.Conforms(type, interfaceType);

	/// <summary>Whether a concrete type satisfies a structural protocol.</summary>
	public bool ConformsToProtocol(TypeSymbol type, ProtocolTypeSymbol protocol)
		=> _protocols.Conforms(type, protocol);

	/// <summary>
	/// Whether more than one distinct method on the type could satisfy a protocol member, so the
	/// caller can report every legitimate candidate rather than silently picking one.
	/// </summary>
	public bool TryFindAmbiguousProtocolMember(TypeSymbol type, ProtocolTypeSymbol protocol, out string? memberName)
		=> _protocols.TryFindAmbiguousMember(type, protocol, out memberName);
}
