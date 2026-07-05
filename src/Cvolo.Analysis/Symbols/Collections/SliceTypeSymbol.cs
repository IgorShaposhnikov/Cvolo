using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Collections;

/// <summary>
/// Formally represents a dynamic slice type (e.g., int[]).
/// internally compiled as the fat pointer structure { ptr, i32 }.
/// </summary>
public sealed class SliceTypeSymbol(TypeSymbol elementType) : TypeSymbol($"{elementType.Name}[]")
{
	public TypeSymbol ElementType { get; } = elementType;
}
