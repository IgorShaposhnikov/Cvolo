using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed record EnumVariantSymbol(string Name, long Value);

public sealed class EnumTypeSymbol(
	string name,
	IReadOnlyList<EnumVariantSymbol> variants,
	TypeSymbol storageType) : TypeSymbol(name)
{
	public TypeSymbol StorageType { get; } = storageType;
	public IReadOnlyList<EnumVariantSymbol> Variants { get; } = variants;

	public bool IsFlags { get; set; }
	public bool IsNonExhaustive { get; set; }
	public bool IsMustUse { get; set; } = false;
	public string? MustUseMessage { get; set; }

	public EnumVariantSymbol? FindVariant(string variantName)
		=> Variants.FirstOrDefault(v => v.Name == variantName);
}
