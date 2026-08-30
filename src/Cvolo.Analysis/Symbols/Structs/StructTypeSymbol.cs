using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class StructTypeSymbol(string name, IReadOnlyList<StructFieldSymbol> fields, StructTypeSymbol? embeddedType = null) : TypeSymbol(name)
{
	public IReadOnlyList<StructFieldSymbol> Fields { get; } = fields;

	/// <summary>
	/// The resolved, flattened embedded type composition this struct
	/// inherits fields from (via the `struct T embed Base` clause), if any.
	/// </summary>
	public StructTypeSymbol? EmbeddedType { get; } = embeddedType;
	/// <summary>
	/// If true, disables auto-inference for extension methods. 
	/// Every extension method must explicitly mark 'ref this' or 'refvar this'.
	/// </summary>
	public bool IsStrictMutability { get; set; } = false;

	public StructFieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);
}
