namespace Cvolo.Analysis;

public sealed class SymbolTable
{
    private readonly SymbolTable? _parent;
    private readonly Dictionary<string, Symbol> _symbols = [];

    public SymbolTable(SymbolTable? parent = null)
    {
        _parent = parent;
    }

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
