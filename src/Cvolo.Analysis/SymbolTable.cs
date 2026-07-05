using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis;

public sealed class SymbolTable(SymbolTable? parent = null)
{
	private readonly SymbolTable? _parent = parent;
	private readonly Dictionary<string, Symbol> _symbols = [];

	public void Declare(Symbol symbol)
	{
		_symbols[symbol.Name] = symbol;
	}

	public Symbol? Lookup(string name)
	{
		if (_symbols.TryGetValue(name, out var symbol))
			return symbol;
		return _parent?.Lookup(name);
	}
}
