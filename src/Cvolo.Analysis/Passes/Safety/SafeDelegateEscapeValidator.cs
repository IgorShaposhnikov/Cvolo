using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns safe-delegate escape policy for non-escaping parameters, return boundaries, and stores into
/// long-lived destinations while provenance classification remains in <see cref="SafeDelegateProvenanceTracker"/>.
/// </summary>
internal sealed class SafeDelegateEscapeValidator(
	BindingContext context,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	/// <summary>Names of non-escaping safe-delegate parameters of the current function (§13.0).</summary>
	private readonly HashSet<string> _delegateParams = [];

	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;

	/// <summary>Clears per-function non-escaping delegate parameter state.</summary>
	public void Reset()
	{
		_delegateParams.Clear();
	}

	/// <summary>Registers a safe-delegate parameter as non-escaping for the current function.</summary>
	public void TrackParameter(string name, TypeSymbol type)
	{
		if (type is DelegateTypeSymbol)
			_delegateParams.Add(name);
	}

	/// <summary>Applies safe-delegate escape rules to a value returned from the current function.</summary>
	public void ValidateReturn(
		ExpressionSyntax expression,
		SymbolTable scope,
		SafeDelegateProvenanceTracker provenance)
	{
		if (provenance.IsDelegateValueExpr(expression, scope))
			CheckDelegateEscape(expression, scope, expression.Span, provenance, isReturn: true);
	}

	/// <summary>
	/// Applies the long-lived-store escape check for an assignment after captured-variable mutation
	/// validation and before provenance propagation.
	/// </summary>
	public void ValidateStore(
		BinaryExpressionSyntax assignment,
		SymbolTable scope,
		SafeDelegateProvenanceTracker provenance)
	{
		if (IsLongLivedStoreDestination(assignment.Left, scope) && provenance.IsDelegateValueExpr(assignment.Right, scope))
			CheckDelegateEscape(assignment.Right, scope, assignment.Span, provenance, isReturn: false);
	}

	/// <summary>
	/// Enforces delegate escape rules at return and long-lived-store boundaries while preserving the
	/// existing diagnostic IDs, text, and ordering.
	/// </summary>
	private void CheckDelegateEscape(
		ExpressionSyntax expr,
		SymbolTable scope,
		TextSpan span,
		SafeDelegateProvenanceTracker provenanceTracker,
		bool isReturn)
	{
		if (expr is not (LambdaExpressionSyntax or MemberAccessExpressionSyntax or IdentifierExpressionSyntax))
			return;

		// Delegate parameters are non-escaping (§13.0): they may be invoked, copied to call-local
		// storage, or forwarded to another non-escaping parameter — never returned or stored long-lived.
		if (expr is IdentifierExpressionSyntax id && _delegateParams.Contains(id.Name))
		{
			context.Diagnostics.Report(context.CurrentUnit!.Context, span,
				isReturn
					? $"Cannot return delegate parameter '{id.Name}': safe-delegate parameters are non-escaping."
					: $"Cannot store delegate parameter '{id.Name}' into long-lived storage: safe-delegate parameters are non-escaping.",
				isReturn ? DiagnosticIds.ParameterReturnedUnsupported : DiagnosticIds.DelegateParameterEscapes);
			return;
		}

		var provenance = provenanceTracker.GetDelegateProvenance(expr, scope);
		switch (provenance.Kind)
		{
			case SafeDelegateProvenanceTracker.DelegateProvenanceKind.Capturing:
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					isReturn
						? $"Captured closure environment cannot escape: the lambda captures '{string.Join(", ", provenance.CapturedNames)}' and may not cross the return boundary."
						: $"Captured closure environment cannot escape: the lambda captures '{string.Join(", ", provenance.CapturedNames)}' and may not be stored into long-lived storage.",
					DiagnosticIds.ClosureEnvironmentEscapes);
				break;
			case SafeDelegateProvenanceTracker.DelegateProvenanceKind.RefBorrow:
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					isReturn
						? $"Reference lambda borrow cannot escape: the lambda borrows '{string.Join(", ", provenance.CapturedNames)}' and may not cross the return boundary."
						: $"Reference lambda borrow cannot escape: the lambda borrows '{string.Join(", ", provenance.CapturedNames)}' and may not be stored into long-lived storage.",
					DiagnosticIds.RefLambdaBorrowEscapes);
				break;
			case SafeDelegateProvenanceTracker.DelegateProvenanceKind.BoundMethod:
				var receiver = provenance.ReceiverBase;
				if (receiver is null)
					return;
				if (scope.Lookup(receiver) is VariableSymbol recvSym)
				{
					if (recvSym.IsGlobal)
						return; // global receiver outlives everything
					context.Diagnostics.Report(context.CurrentUnit!.Context, span,
						$"Bound method on receiver '{receiver}' cannot escape: the receiver does not outlive the delegate.",
						DiagnosticIds.BoundReceiverLifetimeEscapes);
				}

				break;
		}
	}

	/// <summary>
	/// Returns whether an assignment destination outlives local delegate contexts and therefore
	/// requires provenance-free storage.
	/// </summary>
	private bool IsLongLivedStoreDestination(ExpressionSyntax lhs, SymbolTable scope)
	{
		var baseName = _getBaseIdentifierName(lhs);
		if (baseName is null) return false;
		if (scope.Lookup(baseName) is not VariableSymbol dest) return false;
		if (dest.IsGlobal) return true;
		if (dest.Origin == OriginKind.Parameter)
		{
			// Optional reference parameters and writable aggregate parameters with a delegate in their
			// projection are escaping destinations; a by-value non-delegate parameter may be a store.
			return DelegateTypeHelpers.ContainsSafeDelegate(dest.Type);
		}
		return false;
	}
}
