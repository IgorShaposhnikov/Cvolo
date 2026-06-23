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

public sealed class VariableSymbol : Symbol
{
    public TypeSymbol Type { get; }
    public bool IsMutable { get; }

    public VariableSymbol(string name, TypeSymbol type, bool isMutable) : base(name)
    {
        Type = type;
        IsMutable = isMutable;
    }
}
