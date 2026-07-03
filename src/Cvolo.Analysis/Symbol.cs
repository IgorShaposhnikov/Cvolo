namespace Cvolo.Analysis;

public abstract class Symbol(string name)
{
	public string Name { get; } = name;
}

public sealed class FunctionSymbol(
	string name,
	TypeSymbol returnType,
	IReadOnlyList<ParameterSymbol> parameters,
	bool isExtern = false,
	bool isVariadic = false) : Symbol(name)
{
	public TypeSymbol ReturnType { get; } = returnType;
	public IReadOnlyList<ParameterSymbol> Parameters { get; } = parameters;
	public bool IsExtern { get; } = isExtern;
	public bool IsVariadic { get; } = isVariadic;
}

public sealed class ParameterSymbol(string name, TypeSymbol type) : Symbol(name)
{
	public TypeSymbol Type { get; } = type;
}

public sealed class VariableSymbol(string name, TypeSymbol type, bool isMutable) : Symbol(name)
{
	public TypeSymbol Type { get; } = type;
	public bool IsMutable { get; } = isMutable;
	// Track ownership state
	public bool IsMoved { get; set; } = false;
	// Track pointer lifetimes
	public bool PointsToParameter { get; set; } = false;
}
