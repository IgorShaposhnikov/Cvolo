using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class StructFieldSymbol(string name, TypeSymbol type)
{
	public string Name { get; } = name;
	public TypeSymbol Type { get; } = type;
}
