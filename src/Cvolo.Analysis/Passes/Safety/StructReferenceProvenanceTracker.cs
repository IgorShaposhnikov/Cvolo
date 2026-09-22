using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns per-function provenance for struct ref fields, including nested initializer targets and
/// conservative reference targets inferred from resolved call arguments.
/// </summary>
internal sealed class StructReferenceProvenanceTracker(
	BindingContext context,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	private readonly Dictionary<string, HashSet<string>> _structRefTargets = [];
	private readonly Func<ExpressionSyntax, SymbolTable, TypeSymbol?> _resolveExpressionType = resolveExpressionType;
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;

	/// <summary>
	/// Exposes the shared target map consumed by return-time lifetime validation.
	/// </summary>
	internal Dictionary<string, HashSet<string>> Targets => _structRefTargets;

	/// <summary>
	/// Clears all per-function struct reference-field provenance.
	/// </summary>
	public void Reset() => _structRefTargets.Clear();

	/// <summary>
	/// Removes struct reference-field provenance for a variable leaving lexical scope.
	/// </summary>
	public void RemoveVariable(string name) => _structRefTargets.Remove(name);

	/// <summary>
	/// When a struct variable is initialized (struct literal or function call),
	/// scan ref fields and record what each ref field points to in _structRefTargets.
	/// </summary>
	public void TrackStructRefTargets(string varName, ExpressionSyntax initializer, SymbolTable scope)
	{
		var type = _resolveExpressionType(initializer, scope);
		if (type is not StructTypeSymbol structType)
			return;

		var refTargets = new HashSet<string>();
		CollectRefTargets(structType, initializer, scope, refTargets, []);

		if (refTargets.Count > 0)
			_structRefTargets[varName] = refTargets;
	}

	/// <summary>
	/// Collects tracked reference targets from a struct initializer or a resolved call result.
	/// </summary>
	private void CollectRefTargets(StructTypeSymbol structType, ExpressionSyntax expr, SymbolTable scope,
		HashSet<string> targets, HashSet<string> visited)
	{
		if (!visited.Add(structType.Name))
			return; // cycle-cut

		if (expr is StructInitializationExpressionSyntax init)
		{
			foreach (var memberInit in init.Initializers)
			{
				var field = structType.FindField(memberInit.MemberName);
				if (field == null || field.Type is not PointerTypeSymbol ptrType)
					continue;

				var fieldExpr = memberInit.Expression;
				var borrowedName = _getBaseIdentifierName(fieldExpr);
				if (borrowedName != null)
					targets.Add(borrowedName);

				// Recurse into nested struct fields
				if (ptrType.ReferencedType is StructTypeSymbol innerStruct && fieldExpr is StructInitializationExpressionSyntax innerInit)
					CollectRefTargets(innerStruct, innerInit, scope, targets, visited);
			}
		}
		else if (expr is CallExpressionSyntax call && context.ResolvedCalls.TryGetValue(call, out var callee))
		{
			// Function call returning a struct: we can't track per-field origins without interprocedural analysis.
			// Record the function parameters as potential ref targets (conservative).
			for (var i = 0; i < call.Arguments.Count && i < callee.Parameters.Count; i++)
			{
				if (callee.Parameters[i].Type is PointerTypeSymbol)
				{
					var argName = _getBaseIdentifierName(call.Arguments[i]);
					if (argName != null)
						targets.Add(argName);
				}
			}
		}
	}
}
