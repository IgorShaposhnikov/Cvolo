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

		foreach (var field in structType.Fields)
		{
			if (field.IsCycleCut)
				continue;

			if (field.Type is PointerTypeSymbol ptr && ptr.ReferencedType is StructTypeSymbol innerStruct)
			{
				// Ref field pointing to a struct: recurse into that struct's fields
				if (_structRefTargets.TryGetValue(varName, out var targets))
				{
					foreach (var target in targets)
					{
						if (_isDanglingTarget(target, scope))
						{
							context.Diagnostics.Report(context.CurrentUnit!.Context, span,
								$"Cannot return '{varName}' by value: reference field '{field.Name}' targets local variable '{target}' (dangling reference)");
							return;
						}
					}
				}

				VerifyStructByValueReturnCore(innerStruct, varName, span, visited, scope);
			}
			else if (field.Type is PointerTypeSymbol ptrScalar && ptrScalar.ReferencedType is not StructTypeSymbol)
			{
				// Ref field pointing to a scalar: check tracked targets
				if (_structRefTargets.TryGetValue(varName, out var targets))
				{
					foreach (var target in targets)
					{
						if (_isDanglingTarget(target, scope))
						{
							context.Diagnostics.Report(context.CurrentUnit!.Context, span,
								$"Cannot return '{varName}' by value: reference field '{field.Name}' targets local variable '{target}' (dangling reference)");
							return;
						}
					}
				}
			}
		}
	}
}
