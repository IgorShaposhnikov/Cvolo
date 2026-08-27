using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class UnionFieldSymbol(string name, TypeSymbol type, bool isVoidVariant)
{
	public string Name { get; } = name;
	public TypeSymbol Type { get; } = type;
	public bool IsVoidVariant { get; } = isVoidVariant;
}
