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
/// Validates return-time reference lifetimes against the shared provenance state maintained by
/// <see cref="ReferenceLifetimeAnalyzer"/> without introducing an additional syntax traversal.
/// </summary>
internal sealed class ReferenceReturnValidator(
	BindingContext context,
	Dictionary<string, HashSet<string>> structRefTargets,
	Dictionary<string, string> refVarTargets,
	HashSet<string> heapVariables,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName,
	Func<string, bool> hasParentLock,
	Func<SafetyTier> getCurrentTier)
{
	private readonly Dictionary<string, HashSet<string>> _structRefTargets = structRefTargets;
	private readonly Dictionary<string, string> _refVarTargets = refVarTargets;
	private readonly HashSet<string> _heapVariables = heapVariables;
	private readonly Func<ExpressionSyntax, SymbolTable, TypeSymbol?> _resolveExpressionType = resolveExpressionType;
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;
	private readonly Func<string, bool> _hasParentLock = hasParentLock;
	private readonly Func<SafetyTier> _getCurrentTier = getCurrentTier;
	private AggregateReturnLifetimeValidator? _aggregateReturns;

	/// <summary>
	/// Lazily creates the validator for inline pointer-bearing aggregate return values.
	/// </summary>
	private AggregateReturnLifetimeValidator AggregateReturns => _aggregateReturns ??= new AggregateReturnLifetimeValidator(
		context,
		_getBaseIdentifierName,
		IsDanglingTarget,
		ResolveUltimateTarget);

	/// <summary>
	/// Validates reference lifetimes for a return statement, including direct references,
	/// reference-bearing structs, and inline pointer-bearing aggregate values.
	/// </summary>
	public void VerifyReturnLifetime(ReturnStatementSyntax ret, FunctionDeclarationSyntax func, SymbolTable scope)
	{
		if (ret.Expression == null)
			return;

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
		if (_resolveExpressionType(ret.Expression, scope) is { } returnType && AggregateReturnLifetimeValidator.TypeTransitivelyHasRefs(returnType))
			AggregateReturns.VerifyHeapRelativeReturn(ret.Expression, returnType, ret.Expression.Span, scope);
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
			if (field.IsCycleCut)
				continue;

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
		if (_heapVariables.Contains(ultimate))
			return false;
		if (scope.Lookup(ultimate) is not VariableSymbol symbol)
			return false;
		return symbol.Origin == OriginKind.Local;
	}
}
