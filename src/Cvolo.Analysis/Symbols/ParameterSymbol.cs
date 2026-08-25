using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols;

public sealed class ParameterSymbol(string name, TypeSymbol type) : Symbol(name)
{
	public TypeSymbol Type { get; } = type;

	/// <summary>Intrinsic [NoAlias] applied directly to this parameter.</summary>
	public bool IsNoAlias { get; set; }
}
