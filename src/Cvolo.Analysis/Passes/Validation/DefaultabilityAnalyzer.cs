using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Recursive semantic defaultability analysis. Answers two separate questions:
/// ValueDefaultable(T) and CanFormDefault(T, FormationContext, SafetyTier). Deliberately
/// independent of CopyKind: LargeCopy is never a veto, and TrivialCopy is never a proof.
/// Imported aggregates without a verified producer proof return Unverified - never a
/// synthetic Eligible (fail-closed for unproven types).
/// </summary>
public sealed class DefaultabilityAnalyzer
{
	public bool CanFormRawPointerDefault(IFormationContext context, SafetyTier tier)
	{
		if (tier == SafetyTier.Unsafe)
			return true;
		if (context.Kind == FormationContextKind.Embedded)
			return true;
		return false;
	}

	public DefaultPlan GetDefaultPlan(
		TypeSymbol type,
		IFormationContext context,
		SafetyTier tier,
		Func<TypeSymbol, bool>? isImportedType = null,
		Func<TypeSymbol, CopyKind>? classify = null)
		=> Evaluate(type, context, tier, isImportedType, classify, new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance));

	private DefaultPlan Evaluate(
		TypeSymbol type,
		IFormationContext context,
		SafetyTier tier,
		Func<TypeSymbol, bool>? isImportedType,
		Func<TypeSymbol, CopyKind>? classify,
		HashSet<TypeSymbol> visiting)
	{
		if (type is null)
			return Ineligible(TypeSymbol.Int, context, "unknown type");

		if (type.Equals(TypeSymbol.Void))
			return Ineligible(type, context, "void has no default value");

		// ref/refvar: nonnullable references have no default in any mode.
		if (type is PointerTypeSymbol)
			return Ineligible(type, context, "nonnullable ref/refvar has no default");

		if (TypeSymbol.IsIntegerType(type) || type.Equals(TypeSymbol.Bool)
			|| TypeSymbol.IsFloatingPointType(type) || type.Equals(TypeSymbol.Float)
			|| type.Equals(TypeSymbol.Double) || type.Equals(TypeSymbol.NInt)
			|| type.Equals(TypeSymbol.NUInt) || type.Equals(TypeSymbol.Char))
			return Eligible(type, context);

		if (type is RawPointerTypeSymbol)
		{
			if (CanFormRawPointerDefault(context, tier))
				return Eligible(type, context);
			return Ineligible(type, context, "raw pointer default requires an embedded struct-field slot or unsafe");
		}

		// Options are canonical None, never a recursive payload default.
		// An unsafe union matching the Option *shape* is explicitly not a semantic Option.
		if (type is UnionTypeSymbol union)
		{
			if (union.IsUnsafe)
				return Ineligible(type, context, "unsafe unions have no semantic default");
			if (IsAuthorizedOption(union))
				return Eligible(type, context);
			// [Error] unions (try/catch lowering error types) keep their legacy zero default only
			// when every non-void payload variant is itself defaultable under the same recursive
			// rules as structs: no nonnullable references, no destructor payloads, raw pointers
			// only through the embedded field path. Void variants are tag-only.
			if (union.IsErrorUnion)
				return EvaluateErrorUnion(union, context, tier, isImportedType, classify, visiting);
			// G3: a user-defined union that merely *matches* the Option shape (one void
			// variant + one payload variant) is not the authorized canonical Option and
			// must not inherit its None-defaulting semantics.
			if (union.IsOption)
				return Ineligible(type, context, "union matches the Option shape but is not the canonical Option<T>");
			return Ineligible(type, context, "union is not a canonical Option nor an [Error] error union");
		}

		if (type is ArrayTypeSymbol array)
		{
			// Whole-value element-wise defaults; raw-pointer elements are only reachable
			// through an embedded struct-field path (root array rule).
			var elementPlan = Evaluate(array.ElementType, context, tier, isImportedType, classify, visiting);
			if (elementPlan.State == DefaultabilityState.Unverified)
				return new DefaultPlan { Type = type, Context = context, IsEligible = false, Reason = elementPlan.Reason, State = DefaultabilityState.Unverified };
			if (!elementPlan.IsEligible)
				return Ineligible(type, context, $"element type '{array.ElementType.Name}' is not defaultable: {elementPlan.Reason}");
			return Eligible(type, context);
		}

		if (type is StructTypeSymbol structType)
		{
			// Imported aggregates require a verified producer proof. Never infer
			// eligibility from rehydrated field lists or metadata-only destructor placeholders.
			if (isImportedType is not null && isImportedType(type))
				return new DefaultPlan { Type = type, Context = context, IsEligible = false, Reason = "imported type lacks a verified defaultability proof", State = DefaultabilityState.Unverified };

			if (structType.HasDestructor)
				return Ineligible(type, context, "destructor/resource obligations block structural default");

			// Guard against invalid by-value cycles so analysis always terminates.
			if (!visiting.Add(type))
				return Ineligible(type, context, "type contains itself by value");

			try
			{
				foreach (var field in structType.Fields)
				{
					var childPath = new EmbeddedFormationContext($"{type.Name}.{field.Name}");
					var fieldPlan = Evaluate(field.Type, childPath, tier, isImportedType, classify, visiting);
					if (fieldPlan.State == DefaultabilityState.Unverified)
						return new DefaultPlan { Type = type, Context = context, IsEligible = false, Reason = $"field '{field.Name}': {fieldPlan.Reason}", State = DefaultabilityState.Unverified };
					if (!fieldPlan.IsEligible)
						return Ineligible(type, context, $"field '{field.Name}': {fieldPlan.Reason}");
				}

				return Eligible(type, context);
			}
			finally
			{
				visiting.Remove(type);
			}
		}

		if (type is DelegateTypeSymbol { IsNative: true })
		{
			// Native delegate defaults keep their pre-existing explicit unsafe-only semantics;
			// the validator routes these through RequireUnsafe before consulting the plan.
			if (tier == SafetyTier.Unsafe)
				return Eligible(type, context);
			return Ineligible(type, context, "native delegate default requires unsafe");
		}

		if (type is DelegateTypeSymbol)
			return Ineligible(type, context, "delegates are non-null values");

		// Enums zero-initialize to their first variant (variant 0); the compiler's own
		// try/catch/defer lowering synthesizes default(enum) slots, so the enum audit
		// resolves to eligible rather than fail-closed.
		if (type is EnumTypeSymbol)
			return Eligible(type, context);

		// Strings, slices and unspecified builtins fail closed pending a proven
		// semantic recipe; never infer from zero bits.
		return Ineligible(type, context, "type family has no proven default recipe");
	}

	private static DefaultPlan Eligible(TypeSymbol type, IFormationContext context) =>
		new() { Type = type, Context = context, IsEligible = true, State = DefaultabilityState.Eligible };

	private static DefaultPlan Ineligible(TypeSymbol type, IFormationContext context, string reason) =>
		new() { Type = type, Context = context, IsEligible = false, Reason = reason, State = DefaultabilityState.Ineligible };

	/// <summary>
	/// Default plan for an <c>[Error]</c> union (try/catch lowering error type). The legacy zero
	/// default is allowed only when every non-void payload variant is itself defaultable, checked
	/// recursively. Union variant slots are not struct fields, so payloads are evaluated under the
	/// General formation context: a directly-defaulted raw-pointer payload does not get the embedded
	/// privilege (nested struct payloads re-establish their own embedded field paths), a nonnullable
	/// <c>ref</c> payload, a resource/destructor payload or an ineligible nested type is rejected
	/// (fail-closed). Void variants are tag-only.
	/// </summary>
	private DefaultPlan EvaluateErrorUnion(
		UnionTypeSymbol union,
		IFormationContext context,
		SafetyTier tier,
		Func<TypeSymbol, bool>? isImportedType,
		Func<TypeSymbol, CopyKind>? classify,
		HashSet<TypeSymbol> visiting)
	{
		if (!visiting.Add(union))
			return Ineligible(union, context, "type contains itself by value");

		try
		{
			foreach (var field in union.Fields)
			{
				if (field.IsVoidVariant)
					continue;

				var fieldPlan = Evaluate(field.Type, GeneralFormationContext.Instance, tier, isImportedType, classify, visiting);
				if (fieldPlan.State == DefaultabilityState.Unverified)
					return new DefaultPlan { Type = union, Context = context, IsEligible = false, Reason = $"field '{field.Name}': {fieldPlan.Reason}", State = DefaultabilityState.Unverified };
				if (!fieldPlan.IsEligible)
					return Ineligible(union, context, $"field '{field.Name}': {fieldPlan.Reason}");
			}

			return Eligible(union, context);
		}
		finally
		{
			visiting.Remove(union);
		}
	}

	/// <summary>
	/// True only for the compiler-marked canonical <c>Option&lt;T&gt;</c> (Base-SDK) and its
	/// generic instantiations. Authorization comes from the symbol's <see cref="UnionTypeSymbol.IsCanonicalOption"/>
	/// flag, established at declaration/registration and propagated through substitution — never
	/// from a name or shape match.
	/// </summary>
	private static bool IsAuthorizedOption(UnionTypeSymbol union) =>
		union.IsOption && union.IsCanonicalOption;
}
