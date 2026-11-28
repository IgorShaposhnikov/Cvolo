using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Validates tracked struct values returned by value when their reference fields may target
/// stack-local storage, preserving cycle-safe traversal of recursive struct types.
/// </summary>
internal sealed class StructReturnLifetimeValidator(
	BindingContext context,
	Dictionary<string, HashSet<string>> structRefTargets,
	Func<string, SymbolTable, bool> isDanglingTarget)
{
	private readonly Dictionary<string, HashSet<string>> _structRefTargets = structRefTargets;
	private readonly Func<string, SymbolTable, bool> _isDanglingTarget = isDanglingTarget;

	/// <summary>
	/// Verify that a struct being returned by value doesn't have ref fields pointing to local-origin variables (§3C).
	/// Uses cycle detection to handle self-referential structs.
	/// </summary>
	public void VerifyStructByValueReturn(StructTypeSymbol structType, string varName, TextSpan span, SymbolTable scope)
	{
		VerifyStructByValueReturnCore(structType, varName, span, [], scope);
	}

	/// <summary>
	/// Recursively checks reference-bearing fields of a returned struct while cutting type cycles.
	/// </summary>
	private void VerifyStructByValueReturnCore(StructTypeSymbol structType, string varName, TextSpan span, HashSet<string> visited, SymbolTable scope)
	{
		if (!visited.Add(structType.Name))
			return; // cycle-cut: already visited this type, stop recursion

		string? FindDanglingTarget()
		{
			return _structRefTargets.TryGetValue(varName, out var targets)
				? targets.FirstOrDefault(t => _isDanglingTarget(t, scope))
				: null;
		}

		foreach (var field in structType.Fields)
		{
			if (field.IsCycleCut)
				continue;

			var danglingTarget = AggregateReturnLifetimeValidator.TypeTransitivelyHasRefs(field.Type)
				? FindDanglingTarget()
				: null;

			if (danglingTarget is not null)
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					$"Cannot return '{varName}' by value: reference field '{field.Name}' targets local variable '{danglingTarget}' (dangling reference)",
					DiagnosticIds.CannotReturnByValueReferenceFieldTargetsLocalVariableDanglin);

				return;
			}

			if (field.Type is PointerTypeSymbol { ReferencedType: StructTypeSymbol innerStruct })
			{
				VerifyStructByValueReturnCore(innerStruct, varName, span, visited, scope);
			}
			else if (field.Type is StructTypeSymbol nestedStruct)
			{
				VerifyStructByValueReturnCore(nestedStruct, varName, span, visited, scope);
			}
			else if (field.Type is UnionTypeSymbol union)
			{
				foreach (var variant in union.Fields)
				{
					if (!variant.IsVoidVariant && variant.Type is StructTypeSymbol variantStruct)
					{
						VerifyStructByValueReturnCore(variantStruct, varName, span, visited, scope);
					}
				}
			}
		}
	}
}
