using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

/// <summary>
/// A nominal interface type. In the static-only model an interface has no
/// value representation and is never allocated; it exists so an interface-name
/// annotation (e.g. a value/ref parameter typed as an interface) can be
/// recognized and lowered to the concrete conforming type at each call site.
/// </summary>
public sealed class InterfaceTypeSymbol(string name) : TypeSymbol(name)
{
}
