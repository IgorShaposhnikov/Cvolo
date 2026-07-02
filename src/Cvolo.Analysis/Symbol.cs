namespace Cvolo.Analysis;

public abstract class Symbol(string name)
{
    public string Name { get; } = name;
}

public sealed class FunctionSymbol : Symbol
{
    public TypeSymbol ReturnType { get; }
    public IReadOnlyList<ParameterSymbol> Parameters { get; }
    public bool IsExtern { get; }
    public bool IsVariadic { get; }

    public FunctionSymbol(
        string name,
        TypeSymbol returnType,
        IReadOnlyList<ParameterSymbol> parameters,
        bool isExtern = false,
        bool isVariadic = false) : base(name)
    {
        ReturnType = returnType;
        Parameters = parameters;
        IsExtern = isExtern;
        IsVariadic = isVariadic;
    }
}

public sealed class ParameterSymbol : Symbol
{
    public TypeSymbol Type { get; }

    public ParameterSymbol(string name, TypeSymbol type) : base(name)
    {
        Type = type;
    }
}

public sealed class VariableSymbol(string name, TypeSymbol type, bool isMutable) : Symbol(name)
{
    public TypeSymbol Type { get; } = type;
    public bool IsMutable { get; } = isMutable;
    public bool IsMoved { get; set; } = false; // Track ownership state
}
