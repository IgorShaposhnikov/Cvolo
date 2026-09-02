using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class StructTypeSymbol(string name, IReadOnlyList<StructFieldSymbol> fields, StructTypeSymbol? embeddedType = null) : TypeSymbol(name)
{
	private readonly List<StructFieldSymbol> _fields = [.. fields];
	public List<StructFieldSymbol> Fields => _fields;

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

	public bool IsMustUse { get; set; } = false;
	public string? MustUseMessage { get; set; }

	/// <summary>
	/// Populates fields into a placeholder symbol during two-phase struct declaration.
	/// Keeps existing PointerTypeSymbol references to this instance intact.
	/// </summary>
	public void PopulateFields(IEnumerable<StructFieldSymbol> fields)
	{
		_fields.Clear();
		_fields.AddRange(fields);
	}

	public StructFieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);
}
