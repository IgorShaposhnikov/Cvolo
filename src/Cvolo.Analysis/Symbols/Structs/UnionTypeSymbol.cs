using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class UnionTypeSymbol(string name, IReadOnlyList<UnionFieldSymbol> fields) : TypeSymbol(name)
{
	public IReadOnlyList<UnionFieldSymbol> Fields { get; } = fields;

	public UnionFieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);
}
