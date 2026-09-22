using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Provides the small set of read-only expression facts shared by safety analyzers without
/// introducing an additional syntax traversal or broadening their individual responsibilities.
/// </summary>
internal sealed class SafetyExpressionFacts(BindingContext context)
{
	/// <summary>
	/// Resolves the expression forms recognized by safety analysis while preserving the pass's
	/// intentionally partial type-resolution behavior.
	/// </summary>
	public TypeSymbol? ResolveExpressionType(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			IdentifierExpressionSyntax id => scope.Lookup(id.Name) is VariableSymbol v ? v.Type : null,
			CallExpressionSyntax call => context.ResolvedCalls.TryGetValue(call, out var func) ? func.ReturnType : null,
			StructInitializationExpressionSyntax init => context.ResolveType(init.StructTypeName),
			BorrowExpressionSyntax borrow => new PointerTypeSymbol(ResolveExpressionType(borrow.Expression, scope) ?? TypeSymbol.Int, borrow.IsMutable),

			_ => null
		};
	}

	/// <summary>
	/// Peels supported projections and borrows to recover the root identifier used by safety-state tracking.
	/// </summary>
	public string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id) return id.Name;
		if (expr is MemberAccessExpressionSyntax m) return GetBaseIdentifierName(m.Expression);
		if (expr is IndexExpressionSyntax idx) return GetBaseIdentifierName(idx.Left);
		if (expr is BorrowExpressionSyntax b) return GetBaseIdentifierName(b.Expression);
		return null;
	}
}
