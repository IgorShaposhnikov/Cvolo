using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Collections;

public sealed class ArrayTypeSymbol(TypeSymbol elementType, int size) : TypeSymbol($"{elementType.Name}[{size}]")
{
	public TypeSymbol ElementType { get; } = elementType;
	public int Size { get; } = size;
}
