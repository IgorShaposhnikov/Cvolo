using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

/// <summary>
/// A nominal delegate type. A safe delegate is a two-word value
/// { invoke thunk pointer, context pointer }; a native delegate is a
/// single C function pointer. Only the safe family gets the uniform
/// thunk ABI (see the ''Safe Delegates &amp; Borrowed Closures'' increment).
/// Nominal identity is the fully-qualified mangled name plus generic arity;
/// two distinct declarations remain distinct types even with identical
/// signatures, so identity must never reduce to signature shape.
/// </summary>
public sealed class DelegateTypeSymbol : TypeSymbol
{
	/// <summary>The ABI size of a safe delegate value: two native words.</summary>
	public const int SafeDelegateWordCount = 2;

	/// <summary>The required uniform thunk signature parameter list (context pointer first).</summary>
	private static readonly string[] ThunkContextPrefix = ["void*"];

	public TypeSymbol ReturnType { get; }
	public IReadOnlyList<ParameterSymbol> Parameters { get; }

	/// <summary>True for the 'unsafe "C"' native function-pointer family.</summary>
	public bool IsNative { get; }

	/// <summary>Calling convention name for native delegates (e.g. "C"), else null.</summary>
	public string? CallingConvention { get; }

	/// <summary>Generic parameter names for template declarations (empty for non-generic).</summary>
	public IReadOnlyList<string> GenericParameters { get; }

	/// <summary>Concrete type arguments for an instantiated generic delegate (empty otherwise).</summary>
	public IReadOnlyList<TypeSymbol> GenericTypeArguments { get; }

	/// <summary>True when this symbol is the uninstantiated generic template declaration.</summary>
	public bool IsTemplate => GenericParameters.Count > 0;

	/// <summary>True when this symbol is a fully-applied generic instantiation.</summary>
	public bool IsInstantiation => GenericTypeArguments.Count > 0;

	public DelegateTypeSymbol(
		string name,
		TypeSymbol returnType,
		IReadOnlyList<ParameterSymbol> parameters,
		IReadOnlyList<string> genericParameters,
		IReadOnlyList<TypeSymbol> genericTypeArguments,
		bool isNative,
		string? callingConvention)
		: base(name)
	{
		ReturnType = returnType;
		Parameters = parameters;
		GenericParameters = genericParameters;
		GenericTypeArguments = genericTypeArguments;
		IsNative = isNative;
		CallingConvention = callingConvention;
	}

	/// <summary>
	/// Stable nominal identity: fully-qualified mangled name + generic arity.
	/// Independent of signature shape and of the name-only base equality.
	/// </summary>
	private string NominalIdentity => $"{Name}|arity:{GenericParameters.Count}";

	public override bool Equals(object? obj) => obj is DelegateTypeSymbol other && NominalIdentity == other.NominalIdentity;

	public override int GetHashCode() => NominalIdentity.GetHashCode();

	public override string ToString() => $"delegate {ReturnType.Name} {Name}";

	/// <summary>Synthetic parameter list of the uniform thunk: (void* context, P...).</summary>
	public IReadOnlyList<TypeSymbol> ThunkParameterTypes =>
		[new RawPointerTypeSymbol(TypeSymbol.Void), .. Parameters.Select(p => p.Type)];
}