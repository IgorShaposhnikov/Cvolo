using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols;

public sealed class VariableSymbol(string name, TypeSymbol type, bool isMutable) : Symbol(name)
{
	public TypeSymbol Type { get; } = type;
	public bool IsMutable { get; } = isMutable;
	// Track ownership state
	public bool IsMoved { get; set; } = false;
	// Track pointer lifetimes
	public bool PointsToParameter { get; set; } = false;
	public bool IsHeapAllocated { get; set; } = false;
	// Track if the variable has been assigned a value
	public bool IsInitialized { get; set; } = false;
	// Marks data-segment globals ('global lifetime; provenance rules arrive in M2)
	public bool IsGlobal { get; set; } = false;
}
