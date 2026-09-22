using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Validates inline pointer-bearing aggregate return values by collecting every reachable
/// reference payload and checking it against the caller-visible lifetime boundary.
/// </summary>
internal sealed class AggregateReturnLifetimeValidator(
	BindingContext context,
	Func<ExpressionSyntax, string?> getBaseIdentifierName,
	Func<string, SymbolTable, bool> isDanglingTarget,
	Func<string, SymbolTable, string?> resolveUltimateTarget)
{
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;
	private readonly Func<string, SymbolTable, bool> _isDanglingTarget = isDanglingTarget;
	private readonly Func<string, SymbolTable, string?> _resolveUltimateTarget = resolveUltimateTarget;

	/// <summary>
	/// Returns whether a type contains reference-bearing storage directly or through value aggregates.
	/// </summary>
	internal static bool TypeTransitivelyHasRefs(TypeSymbol type)
	{
		return type switch
		{
			PointerTypeSymbol => true,
			StructTypeSymbol st => st.Fields.Any(f => TypeTransitivelyHasRefs(f.Type)),
			UnionTypeSymbol ut => ut.Fields.Any(f => !f.IsVoidVariant && TypeTransitivelyHasRefs(f.Type)),
			ArrayTypeSymbol arr => TypeTransitivelyHasRefs(arr.ElementType),
			SliceTypeSymbol sl => TypeTransitivelyHasRefs(sl.ElementType),
			_ => false
		};
	}

	/// <summary>
	/// Verify a pointer-bearing value returned by value: every reachable reference payload must not
	/// dangle. Collects the base identifiers of all reference payloads in the expression, then checks
	/// each one against heap/global/parameter provenance.
	/// </summary>
	public void VerifyHeapRelativeReturn(ExpressionSyntax retExpr, TypeSymbol retType, TextSpan span, SymbolTable scope)
	{
		var targets = new HashSet<string>();
		CollectPointerPayloadBases(retExpr, retType, scope, targets, []);

		foreach (var target in targets)
		{
			if (_isDanglingTarget(target, scope))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					$"Cannot return value: reference '{target}' targets local variable '{_resolveUltimateTarget(target, scope)}' (dangling reference)");
				return;
			}
		}
	}

	/// <summary>
	/// Collects base identifiers for reference payloads reachable from an expression of the supplied type.
	/// </summary>
	private void CollectPointerPayloadBases(ExpressionSyntax expr, TypeSymbol type, SymbolTable scope,
		HashSet<string> targets, HashSet<string> visited)
	{
		if (type is PointerTypeSymbol)
		{
			var baseId = _getBaseIdentifierName(expr);
			if (baseId != null)
				targets.Add(baseId);
			return;
		}

		if (expr is StructInitializationExpressionSyntax init)
		{
			if (type is StructTypeSymbol st)
			{
				if (!visited.Add("S:" + st.Name))
					return;
				foreach (var memberInit in init.Initializers)
				{
					var field = st.FindField(memberInit.MemberName);
					if (field == null)
						continue;
					if (field.Type is PointerTypeSymbol)
					{
						var baseId = _getBaseIdentifierName(memberInit.Expression);
						if (baseId != null)
							targets.Add(baseId);
					}
					else if (field.Type is StructTypeSymbol or UnionTypeSymbol)
					{
						CollectPointerPayloadBases(memberInit.Expression, field.Type, scope, targets, visited);
					}
				}
			}
			else if (type is UnionTypeSymbol ut && init.Initializers.Count == 1)
			{
				var variant = ut.FindField(init.Initializers[0].MemberName);
				if (variant == null || variant.IsVoidVariant)
					return;
				if (variant.Type is PointerTypeSymbol)
				{
					var baseId2 = _getBaseIdentifierName(init.Initializers[0].Expression);
					if (baseId2 != null)
						targets.Add(baseId2);
				}
				else if (variant.Type is StructTypeSymbol or UnionTypeSymbol)
				{
					CollectPointerPayloadBases(init.Initializers[0].Expression, variant.Type, scope, targets, visited);
				}
			}

			return;
		}

		// Non-literal pointer-bearing expression (reference-field member access, graph-handle call, or
		// a reference/option variable): fall back to the base identifier; chains resolve via reference provenance.
		var baseId3 = _getBaseIdentifierName(expr);
		if (baseId3 != null)
			targets.Add(baseId3);
	}
}
