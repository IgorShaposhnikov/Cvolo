namespace Cvolo.Analysis;

public sealed class ArrayTypeSymbol(TypeSymbol elementType, int size) : TypeSymbol($"{elementType.Name}[{size}]")
{
	public TypeSymbol ElementType { get; } = elementType;
	public int Size { get; } = size;
}
