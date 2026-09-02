using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class UnionFieldSymbol(string name, TypeSymbol type, bool isVoidVariant)
{
	public string Name { get; } = name;
	public TypeSymbol Type { get; } = type;
	public bool IsVoidVariant { get; } = isVoidVariant;

	/// <summary>A union variant inherits the visibility tier of its parent union declaration.</summary>
	public Visibility Visibility { get; set; } = Visibility.Internal;
}
