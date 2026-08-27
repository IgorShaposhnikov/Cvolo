using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

/// <summary>
/// Represents a raw pointer type (T*) — distinct from PointerTypeSymbol (ref/refvar T).
/// Only valid inside unsafe contexts.
/// </summary>
public sealed class RawPointerTypeSymbol(TypeSymbol elementType) : TypeSymbol($"{elementType.Name}*")
{
	public TypeSymbol ElementType { get; } = elementType;

	public override string Name => $"{ElementType.Name}*";
}
