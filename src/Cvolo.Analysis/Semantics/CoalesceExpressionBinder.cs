using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Selects the single semantic operation represented by a fallback (<c>left ?? right</c>) expression.
/// </summary>
/// <remarks>
/// <para>
/// The binder is intentionally side-effect free: the validation layer decides when the declaration
/// universe is stable, stores the returned descriptor in
/// <see cref="BindingContext.ResolvedCoalesceExpressions"/>, and reports any diagnostic exactly once.
/// </para>
/// <para>
/// The left operand must already be an Option. Plain values, raw pointers and native delegates are
/// never implicitly lifted to an Option, because the operator has no way to know which absence a
/// non-Option value was meant to express.
/// </para>
/// <para>
/// <paramref name="resultTarget"/> is the surrounding target type, when the fallback sits in a
/// position that has one. It never helps the left operand become an Option — the operator stays
/// left-driven — it only chooses between the two result modes when both readings of the right
/// operand would otherwise be legal.
/// </para>
/// </remarks>
internal static class CoalesceExpressionBinder
{
	internal static ResolvedCoalesceExpression Bind(
		CoalesceExpressionSyntax syntax,
		TypeSymbol? leftType,
		TypeSymbol? rightType,
		TypeSymbol? resultTarget = null)
	{
		// The left operand is only inspectable once its own type is known. A left that is known and
		// not an Option is a hard error regardless of the right operand.
		if (leftType is null || rightType is null)
			return Error(syntax, leftType, rightType, CoalesceBindingErrorKind.UnresolvedOperand);

		if (leftType is not UnionTypeSymbol { IsOption: true } optionType || optionType.PayloadVariant is not { } payload)
			return Error(syntax, leftType, rightType, CoalesceBindingErrorKind.LeftOperandNotOption);

		var payloadType = payload.Type;

		// A target that keeps the Option layer disambiguates between the two readings of a
		// payload-compatible right operand: `int? b = opt ?? 0;` wants the fallback wrapped in
		// `Some`, so the result is `Option<int>` rather than the extracted `int` the target could
		// not accept. This never rescues an unusable left operand — that is checked above — so the
		// operator remains left-driven.
		var preservesTargetOption = resultTarget is UnionTypeSymbol { IsOption: true } targetOption
			&& targetOption.PayloadVariant is { } targetPayload
			&& TypesMatch(targetPayload.Type, payloadType);

		// `T` is tried first, then `Option<T>`. Trying the bare payload first is what makes
		// `someOption ?? 0` extract the payload while `someOption ?? noneOption` preserves the layer.
		if (TypesMatch(rightType, payloadType))
		{
			return Success(
				syntax,
				optionType,
				leftType,
				rightType,
				payloadType,
				preservesTargetOption
					? CoalesceResultMode.PreserveOptionalOrFallback
					: CoalesceResultMode.ExtractPayloadOrFallback,
				CoalesceRightConversion.Payload,
				preservesTargetOption ? resultTarget! : payloadType);
		}

		if (rightType is UnionTypeSymbol { IsOption: true } rightOption
			&& rightOption.PayloadVariant is { } rightPayload
			&& TypesMatch(rightPayload.Type, payloadType))
		{
			return Success(
				syntax,
				optionType,
				leftType,
				rightType,
				payloadType,
				CoalesceResultMode.PreserveOptionalOrFallback,
				CoalesceRightConversion.Optional,
				optionType);
		}

		// The payload type is retained even on failure so the diagnostic can name what the right
		// operand was expected to produce.
		return Error(syntax, leftType, rightType, CoalesceBindingErrorKind.RightOperandNotCompatible,
			optionType, payloadType);
	}

	/// <summary>
	/// Compares two payload types for fallback matching. Payload matching is by type identity, with
	/// the caveat that both Options' payloads are compared structurally: <c>Option&lt;int&gt;</c> and
	/// <c>Option&lt;int&gt;</c> name the same instantiation, while <c>Option&lt;int&gt;</c> and
	/// <c>Option&lt;long&gt;</c> do not, even though the two unions are the same generic declaration.
	/// </summary>
	private static bool TypesMatch(TypeSymbol candidate, TypeSymbol payloadType) =>
		candidate.Equals(payloadType);

	private static ResolvedCoalesceExpression Success(
		CoalesceExpressionSyntax syntax,
		UnionTypeSymbol optionType,
		TypeSymbol leftType,
		TypeSymbol rightType,
		TypeSymbol payloadType,
		CoalesceResultMode mode,
		CoalesceRightConversion rightConversion,
		TypeSymbol resultType) =>
		new(syntax, optionType, leftType, rightType, payloadType, mode, rightConversion, resultType, null);

	private static ResolvedCoalesceExpression Error(
		CoalesceExpressionSyntax syntax,
		TypeSymbol? leftType,
		TypeSymbol? rightType,
		CoalesceBindingErrorKind errorKind,
		UnionTypeSymbol? optionType = null,
		TypeSymbol? payloadType = null) =>
		new(syntax, optionType, leftType, rightType, payloadType, null, null, null, errorKind);
}
