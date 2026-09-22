using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Tracks ordinary reference provenance for one function and validates that reference-bearing
/// return values do not outlive stack-local storage. Safe-delegate provenance is intentionally
/// owned by <see cref="Cvolo.Analysis.Passes.SafetyPass"/> and is not part of this service.
/// </summary>
internal sealed class ReferenceLifetimeAnalyzer(
	BindingContext context,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName,
	Func<string, bool> hasParentLock,
	Func<SafetyTier> getCurrentTier)
{
	private readonly Dictionary<string, HashSet<string>> _structRefTargets = [];
	private readonly Dictionary<string, string> _refVarTargets = [];
	private readonly HashSet<string> _heapVariables = [];
	private readonly Func<ExpressionSyntax, SymbolTable, TypeSymbol?> _resolveExpressionType = resolveExpressionType;
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;
	private readonly Func<string, bool> _hasParentLock = hasParentLock;
	private readonly Func<SafetyTier> _getCurrentTier = getCurrentTier;
	private ReferenceReturnValidator? _returnValidator;

	/// <summary>
	/// Lazily creates the validator that consumes the shared reference-provenance state for return checks.
	/// </summary>
	private ReferenceReturnValidator ReturnValidator => _returnValidator ??= new ReferenceReturnValidator(
		context,
		_structRefTargets,
		_refVarTargets,
		_heapVariables,
		_resolveExpressionType,
		_getBaseIdentifierName,
		_hasParentLock,
		_getCurrentTier);

	/// <summary>Clears all per-function reference provenance state.</summary>
	public void Reset()
	{
		_structRefTargets.Clear();
		_refVarTargets.Clear();
		_heapVariables.Clear();
	}

	/// <summary>
	/// Removes block-scoped provenance associated with a reference variable leaving scope.
	/// Heap-owner state is intentionally retained for the duration of the function, matching
	/// the previous pass behavior.
	/// </summary>
	public void RemoveVariable(string name)
	{
		_structRefTargets.Remove(name);
		_refVarTargets.Remove(name);
	}

	/// <summary>Marks a local as owning heap-backed storage that may safely outlive the function.</summary>
	public void MarkHeapVariable(string name) => _heapVariables.Add(name);

	/// <summary>
	/// Records reference provenance introduced by a variable declaration, including origin
	/// propagation for ref/refvar borrows, heap ownership, reference chains, and struct ref fields.
	/// </summary>
	public void TrackDeclaration(VariableDeclarationSyntax declaration, VariableSymbol symbol, SymbolTable scope)
	{
		if (declaration.Initializer is not { } initializer)
			return;

		if (declaration.Type is "ref" or "refvar" && initializer is BorrowExpressionSyntax borrowExpr)
		{
			var borrowedName = _getBaseIdentifierName(borrowExpr.Expression);
			if (borrowedName != null && scope.Lookup(borrowedName) is VariableSymbol borrowed)
				symbol.Origin = borrowed.Origin;
		}

		if (initializer is HeapAllocationExpressionSyntax)
			MarkHeapVariable(declaration.Name);

		if (IsTrackedReferenceType(symbol.Type))
			TrackReferenceTarget(declaration.Name, initializer, scope, clearWhenMissing: false);

		if (declaration.Type is not "ref" and not "refvar" && symbol.Type is StructTypeSymbol)
			TrackStructRefTargets(declaration.Name, initializer, scope);
	}

	/// <summary>
	/// Updates reference provenance after assignment to a named variable and enforces the global
	/// lifetime inequality for ref/refvar values.
	/// </summary>
	public void TrackAssignment(
		IdentifierExpressionSyntax left,
		VariableSymbol leftSymbol,
		ExpressionSyntax right,
		TextSpan span,
		SymbolTable scope)
	{
		if (leftSymbol.Type is StructTypeSymbol)
			TrackStructRefTargets(left.Name, right, scope);

		if (IsTrackedReferenceType(leftSymbol.Type))
			TrackReferenceTarget(left.Name, right, scope, clearWhenMissing: true);

		if (leftSymbol.Type is not PointerTypeSymbol)
			return;

		VariableSymbol? rightSymbol = null;
		if (right is BorrowExpressionSyntax borrow)
		{
			var rightName = _getBaseIdentifierName(borrow.Expression);
			if (rightName != null)
				rightSymbol = scope.Lookup(rightName) as VariableSymbol;
		}
		else if (right is IdentifierExpressionSyntax rightId
			&& scope.Lookup(rightId.Name) is VariableSymbol candidate
			&& candidate.Type is PointerTypeSymbol)
		{
			rightSymbol = candidate;
		}

		if (rightSymbol is null)
			return;

		leftSymbol.Origin = rightSymbol.Origin;
		if (leftSymbol.IsGlobal && rightSymbol.Origin != OriginKind.Global)
		{
			context.Diagnostics.Report(context.CurrentUnit!.Context, span,
				$"Cannot assign {rightSymbol.Origin.ToString().ToLower()}-origin reference to global variable '{left.Name}': only global-origin references may be stored in globals");
		}
	}

	/// <summary>Returns whether a type participates in tracked reference-chain provenance.</summary>
	private static bool IsTrackedReferenceType(TypeSymbol type)
	{
		return type is PointerTypeSymbol
			|| type is UnionTypeSymbol { IsOption: true, IsNpoEligible: true };
	}

	/// <summary>
	/// Records the base storage targeted by a ref/refvar or nullable-reference-option value.
	/// Reassignments may request removal when the new value has no trackable payload.
	/// </summary>
	public void TrackReferenceTarget(string name, ExpressionSyntax expression, SymbolTable scope, bool clearWhenMissing)
	{
		var targetBase = TryGetPayloadBase(expression, scope);
		if (targetBase != null)
			_refVarTargets[name] = targetBase;
		else if (clearWhenMissing)
			_refVarTargets.Remove(name);
	}

	/// <summary>
	/// Validates reference lifetimes for a return statement using the shared per-function provenance state.
	/// </summary>
	public void VerifyReturnLifetime(ReturnStatementSyntax ret, FunctionDeclarationSyntax func, SymbolTable scope)
	{
		ReturnValidator.VerifyReturnLifetime(ret, func, scope);
	}

	/// <summary>
	/// For a ref/refvar or nullable-reference-option initializer, return the base identifier the
	/// reference ultimately points at (the payload expression for a reference option literal).
	/// </summary>
	private string? TryGetPayloadBase(ExpressionSyntax expr, SymbolTable scope)
	{
		if (expr is StructInitializationExpressionSyntax init && init.Initializers.Count == 1)
		{
			if (_resolveExpressionType(init, scope) is UnionTypeSymbol ut)
			{
				var variant = ut.FindField(init.Initializers[0].MemberName);
				if (variant?.Type is PointerTypeSymbol)
					return _getBaseIdentifierName(init.Initializers[0].Expression);
				return null;
			}
		}

		return _getBaseIdentifierName(expr);
	}

	/// <summary>
	/// When a struct variable is initialized (struct literal or function call),
	/// scan ref fields and record what each ref field points to in _structRefTargets.
	/// </summary>
	public void TrackStructRefTargets(string varName, ExpressionSyntax initializer, SymbolTable scope)
	{
		var type = _resolveExpressionType(initializer, scope);
		if (type is not StructTypeSymbol structType) return;

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
		if (!visited.Add(structType.Name)) return; // cycle-cut

		if (expr is StructInitializationExpressionSyntax init)
		{
			foreach (var memberInit in init.Initializers)
			{
				var field = structType.FindField(memberInit.MemberName);
				if (field == null || field.Type is not PointerTypeSymbol ptrType) continue;

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
