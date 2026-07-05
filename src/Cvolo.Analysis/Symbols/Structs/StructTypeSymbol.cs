using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class StructTypeSymbol(string name, IReadOnlyList<StructFieldSymbol> fields) : TypeSymbol(name)
{
	public IReadOnlyList<StructFieldSymbol> Fields { get; } = fields;

	public StructFieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);
}
