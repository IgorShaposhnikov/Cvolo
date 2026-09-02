using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Symbols;

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

	/// <summary>
	/// Intrinsic [UnsafeBody] marker - SafetyPass treats the body as unsafe (consumed by the unmanaged milestone).
	/// </summary>
	public bool IsUnsafeBody { get; set; }

	/// <summary>
	/// Intrinsic [NoAlias] marker - emitter attaches LLVM noalias to reference params (unbound/unsafe tiers).
	/// </summary>
	public bool IsNoAlias { get; set; }

	/// <summary>
	/// Target LLVM intrinsic name when decorated with [Intrinsic("llvm.name")].
	/// </summary>
	public string? IntrinsicName { get; set; }

	/// <summary>
	/// Warning ids suppressed via [SuppressWarning("id")] on this declaration.
	/// </summary>
	public List<string> SuppressedWarnings { get; } = [];

	/// <summary>
	/// Safety tier: Safe (default), Unbound, or Unsafe. Set from function modifier or [UnsafeBody] attribute.
	/// </summary>
	public SafetyTier SafetyTier { get; set; }
	/// <summary>
	/// Intrinsic [MustUse] marker — callers must not discard the returned value.
	/// </summary>
	public bool IsMustUse { get; set; }
	public string? MustUseMessage { get; set; }
}
