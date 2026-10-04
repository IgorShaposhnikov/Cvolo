using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// How the fallback operator combines its left payload with its right operand.
/// </summary>
public enum CoalesceResultMode
{
	/// <summary>
	/// The right operand produced the payload type <c>T</c>. The expression yields that bare payload,
	/// so a <c>None</c> left operand is unwrapped and the Option layer does not survive.
	/// </summary>
	ExtractPayloadOrFallback,

	/// <summary>
	/// The right operand produced <c>Option&lt;T&gt;</c>. The expression yields an Option, so a
	/// <c>Some</c> left operand is re-wrapped and the Option layer is preserved. Exactly one Option
	/// layer is handled; the result is never flattened recursively.
	/// </summary>
	PreserveOptionalOrFallback,
}

/// <summary>
/// How the right operand converts to the left operand's payload type.
/// </summary>
public enum CoalesceRightConversion
{
	/// <summary>The right operand already has the payload type <c>T</c>.</summary>
	Payload,

	/// <summary>The right operand already has type <c>Option&lt;T&gt;</c>.</summary>
	Optional,
}

/// <summary>
/// Why a fallback expression could not be bound.
/// </summary>
public enum CoalesceBindingErrorKind
{
	/// <summary>An operand's type is not known yet; the binding is incomplete, not invalid.</summary>
	UnresolvedOperand,

	/// <summary>The left operand's type is not an Option, so it has no payload to unwrap.</summary>
	LeftOperandNotOption,

	/// <summary>
	/// The right operand matches neither the payload type <c>T</c> nor <c>Option&lt;T&gt;</c>.
	/// </summary>
	RightOperandNotCompatible,
}

/// <summary>
/// The canonical semantic descriptor for a fallback (<c>left ?? right</c>) expression.
/// </summary>
/// <remarks>
/// Validation resolves the descriptor once and caches it in
/// <see cref="BindingContext.ResolvedCoalesceExpressions"/>. Type lookup, code generation and tooling
/// consume this descriptor instead of re-deriving the Option shape from source-level syntax, so the
/// runtime lowering and the compile-time result type cannot disagree.
/// </remarks>
public sealed class ResolvedCoalesceExpression
{
	internal ResolvedCoalesceExpression(
		CoalesceExpressionSyntax syntax,
		UnionTypeSymbol? optionType,
		TypeSymbol? leftType,
		TypeSymbol? rightType,
		TypeSymbol? payloadType,
		CoalesceResultMode? mode,
		CoalesceRightConversion? rightConversion,
		TypeSymbol? resultType,
		CoalesceBindingErrorKind? errorKind)
	{
		Syntax = syntax;
		OptionType = optionType;
		LeftType = leftType;
		RightType = rightType;
		PayloadType = payloadType;
		Mode = mode;
		RightConversion = rightConversion;
		ResultType = resultType;
		ErrorKind = errorKind;
	}

	/// <summary>The expression this descriptor was resolved from.</summary>
	public CoalesceExpressionSyntax Syntax { get; }

	/// <summary>The left operand's Option type, or <see langword="null"/> when binding failed.</summary>
	public UnionTypeSymbol? OptionType { get; }

	/// <summary>The resolved left operand type, or <see langword="null"/> when unknown.</summary>
	public TypeSymbol? LeftType { get; }

	/// <summary>The resolved right operand type, or <see langword="null"/> when unknown.</summary>
	public TypeSymbol? RightType { get; }

	/// <summary>
	/// The payload type <c>T</c> carried by the left Option. This is the type the right operand is
	/// matched against, and the type produced in <see cref="CoalesceResultMode.ExtractPayloadOrFallback"/>.
	/// </summary>
	public TypeSymbol? PayloadType { get; }

	/// <summary>The result combination mode, or <see langword="null"/> when binding failed.</summary>
	public CoalesceResultMode? Mode { get; }

	/// <summary>How the right operand relates to the payload type, or <see langword="null"/> on failure.</summary>
	public CoalesceRightConversion? RightConversion { get; }

	/// <summary>
	/// The expression's result type: the payload type <c>T</c> in extract mode, or
	/// <c>Option&lt;T&gt;</c> in preserve mode.
	/// </summary>
	public TypeSymbol? ResultType { get; }

	/// <summary>Why binding failed, or <see langword="null"/> when it succeeded.</summary>
	public CoalesceBindingErrorKind? ErrorKind { get; }

	/// <summary>True when this descriptor represents a usable binding.</summary>
	public bool IsSuccess => ErrorKind is null;

	/// <summary>
	/// True when the Option is lowered to a bare pointer by the Null-Pointer Optimization, so code
	/// generation tests the pointer against null instead of reading a discriminant.
	/// </summary>
	public bool UsesNpoRepresentation => OptionType?.IsNpoEligible ?? false;

	/// <summary>
	/// True when the result keeps an Option layer, so the runtime must materialise a real
	/// <c>Option&lt;T&gt;</c> even when the right operand supplied a bare payload.
	/// </summary>
	public bool ProducesOptional => Mode == CoalesceResultMode.PreserveOptionalOrFallback || ProducesOptionalOnNonePath;

	/// <summary>
	/// True when a bare-payload right operand must be wrapped in <c>Some</c> on the <c>None</c> path to
	/// satisfy a preserving result type.
	/// </summary>
	public bool ProducesOptionalOnNonePath => Mode == CoalesceResultMode.PreserveOptionalOrFallback && RightConversion == CoalesceRightConversion.Payload;
}
