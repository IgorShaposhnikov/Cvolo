using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Result of binding a lambda expression against an expected delegate type (§4/§5).
/// Carries everything the safety pass and code generator need: target delegate,
/// capture mode, resolved parameter types and the body return type.
/// </summary>
public sealed class LambdaBindingInfo
{
	public DelegateTypeSymbol Delegate { get; init; } = null!;
	public LambdaCaptureMode CaptureMode { get; init; }
	public IReadOnlyList<TypeSymbol> ParameterTypes { get; init; } = Array.Empty<TypeSymbol>();
	public TypeSymbol ReturnType { get; init; } = TypeSymbol.Void;
	public bool BodyIsValueExpression { get; init; }
}