using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols;

public sealed class VariableSymbol(string name, TypeSymbol type, bool isMutable) : Symbol(name)
{
	public TypeSymbol Type { get; } = type;
	public bool IsMutable { get; } = isMutable;
	public bool IsMoved { get; set; } = false;
	public OriginKind Origin { get; set; } = OriginKind.Local;
	public bool IsHeapAllocated { get; set; } = false;
	public bool IsInitialized { get; set; } = false;
	public bool IsGlobal { get; set; } = false;
	public bool IsRawPointer { get; set; } = false;

	/// <summary>The namespace that declared this global; null for the global (root) namespace.</summary>
	public string? DeclaringNamespace { get; set; }

	/// <summary>Fully-qualified name ("Ns.Sub.Name", or just "Name" for the root namespace).</summary>
	public string QualifiedGlobalName =>
		string.IsNullOrEmpty(DeclaringNamespace) ? Name : $"{DeclaringNamespace}.{Name}";
}
