using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
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
	/// Validates reference lifetimes for a return statement, including direct references,
	/// reference-bearing structs, and inline pointer-bearing aggregate values.
	/// </summary>
	public void VerifyReturnLifetime(ReturnStatementSyntax ret, FunctionDeclarationSyntax func, SymbolTable scope)
	{
		if (ret.Expression == null) return;

		// Lifetime checks are disabled in unsafe tier
		if (_getCurrentTier() == SafetyTier.Unsafe)
			return;

		// Case 1: return ref expr; — BorrowExpressionSyntax wrapping an identifier
		if (ret.Expression is BorrowExpressionSyntax borrow && borrow.Expression is IdentifierExpressionSyntax bid)
		{
			if (IsDanglingTarget(bid.Name, scope))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span, $"Cannot return reference to local variable '{bid.Name}' (dangling reference)");
			}
			return;
		}

		// Case 2: return r; where r is a ref/refvar variable (PointerTypeSymbol)
		if (ret.Expression is IdentifierExpressionSyntax id)
		{
			if (scope.Lookup(id.Name) is VariableSymbol idSym && idSym.Type is PointerTypeSymbol && IsDanglingTarget(id.Name, scope))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span, $"Cannot return reference to local variable '{id.Name}' (dangling reference)");
			}

			// Case 3: return by value of a variable whose fields are currently borrowed
			if (_hasParentLock(id.Name))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span,
					$"Cannot return '{id.Name}' by value while a field borrow is still active");
			}

			// Case 4: return by value of a struct whose ref fields point to locals (§3C)
			if (scope.Lookup(id.Name) is VariableSymbol retSym && retSym.Type is StructTypeSymbol retStruct)
			{
				VerifyStructByValueReturn(retStruct, id.Name, ret.Expression.Span, scope);
			}

			return;
		}

		// Case 5: return a pointer-bearing value constructed inline (struct literal, non-nullable
		// reference option literal, or a reference-field member access). Every reachable reference
		// payload must ultimately point at heap, global, or parameter storage; a non-heap stack local
		// would dangle once the caller takes ownership of the returned graph (heap-relative provenance).
		if (_resolveExpressionType(ret.Expression, scope) is { } returnType && TypeTransitivelyHasRefs(returnType))
			VerifyHeapRelativeReturn(ret.Expression, returnType, ret.Expression.Span, scope);
	}

	/// <summary>
	/// Verify that a struct being returned by value doesn't have ref fields pointing to local-origin variables (§3C).
	/// Uses cycle detection to handle self-referential structs.
	/// </summary>
	private void VerifyStructByValueReturn(StructTypeSymbol structType, string varName, TextSpan span, SymbolTable scope)
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
			if (field.IsCycleCut) continue;

			if (field.Type is PointerTypeSymbol ptr && ptr.ReferencedType is StructTypeSymbol innerStruct)
			{
				// Ref field pointing to a struct: recurse into that struct's fields
				if (_structRefTargets.TryGetValue(varName, out var targets))
				{
					foreach (var target in targets)
					{
						if (IsDanglingTarget(target, scope))
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
						if (IsDanglingTarget(target, scope))
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
	/// Resolve a variable name through reference chains to the concrete variable whose storage the
	/// reference ultimately points at (heap-relative provenance). Cycle-safe.
	/// </summary>
	private string? ResolveUltimateTarget(string name, SymbolTable scope)
	{
		var visited = new HashSet<string>();
		var current = name;
		while (current != null && visited.Add(current) && _refVarTargets.TryGetValue(current, out var next))
			current = next;
		return current;
	}

	/// <summary>
	/// True when a reference target points at a non-heap stack-local variable that would dangle once
	/// ownership of the returned graph transfers to a caller. Heap allocations, globals, and parameters
	/// all outlive the function and are therefore safe escape targets.
	/// </summary>
	private bool IsDanglingTarget(string targetName, SymbolTable scope)
	{
		var ultimate = ResolveUltimateTarget(targetName, scope) ?? targetName;
		if (_heapVariables.Contains(ultimate)) return false;
		if (scope.Lookup(ultimate) is not VariableSymbol symbol) return false;
		return symbol.Origin == OriginKind.Local;
	}

	/// <summary>
	/// Returns whether a type contains reference-bearing storage directly or through value aggregates.
	/// </summary>
	private static bool TypeTransitivelyHasRefs(TypeSymbol type)
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
	private void VerifyHeapRelativeReturn(ExpressionSyntax retExpr, TypeSymbol retType, TextSpan span, SymbolTable scope)
	{
		var targets = new HashSet<string>();
		CollectPointerPayloadBases(retExpr, retType, scope, targets, []);

		foreach (var target in targets)
		{
			if (IsDanglingTarget(target, scope))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					$"Cannot return value: reference '{target}' targets local variable '{ResolveUltimateTarget(target, scope)}' (dangling reference)");
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
				if (!visited.Add("S:" + st.Name)) return;
				foreach (var memberInit in init.Initializers)
				{
					var field = st.FindField(memberInit.MemberName);
					if (field == null) continue;
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
				if (variant == null || variant.IsVoidVariant) return;
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
		// a reference/option variable): fall back to the base identifier; chains resolve via _refVarTargets.
		var baseId3 = _getBaseIdentifierName(expr);
		if (baseId3 != null)
			targets.Add(baseId3);
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
