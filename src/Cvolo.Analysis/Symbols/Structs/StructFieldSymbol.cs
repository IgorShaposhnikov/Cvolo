using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class StructFieldSymbol(string name, TypeSymbol type)
{
	public string Name { get; } = name;
	public TypeSymbol Type { get; } = type;
	public OriginKind Origin { get; set; } = OriginKind.Local;
	public bool IsCycleCut { get; set; } = false;

	/// <summary>The field's visibility tier; defaults to private per the Strict Safe Defaults rule.</summary>
	public Visibility Visibility { get; set; } = Visibility.Private;
}
