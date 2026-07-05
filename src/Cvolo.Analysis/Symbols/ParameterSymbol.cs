using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols;

public sealed class ParameterSymbol(string name, TypeSymbol type) : Symbol(name)
{
	public TypeSymbol Type { get; } = type;
}
