using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns safe-delegate provenance propagation and escape diagnostics while lambda capture policy
/// is delegated to <see cref="LambdaCaptureAnalyzer"/>.
/// </summary>
internal sealed class SafeDelegateAnalyzer(
	BindingContext context,
	LambdaCaptureAnalyzer lambdaCaptures,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	/// <summary>
	/// Delegate provenance records for delegate-typed variables declared so far in the current
	/// function. Copy assignments propagate the carried closure or receiver context.
	/// </summary>
	private readonly Dictionary<string, DelegateProvenance> _delegateProvenances = [];

	/// <summary>Names of non-escaping safe-delegate parameters of the current function (§13.0).</summary>
	private readonly HashSet<string> _delegateParams = [];

	/// <summary>Names of delegate-typed locals/globals whose context must not escape.</summary>
	private readonly Dictionary<string, DelegateProvenance> _nonEscapingDelegates = [];

	private readonly Func<ExpressionSyntax, SymbolTable, TypeSymbol?> _resolveExpressionType = resolveExpressionType;
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;

	private enum DelegateProvenanceKind
	{
		Free,       // context == null (free function / non-capturing lambda): may escape
		Capturing,  // context == &closureEnv: may NOT escape (CVL1318)
		RefBorrow,  // context == &closureEnv holding ref borrows: may NOT escape (CVL1316)
		BoundMethod,// context == &receiver: may escape only as far as the receiver (CVL1321/CVL1319)
	}

	private sealed record DelegateProvenance(
		DelegateProvenanceKind Kind,
		IReadOnlyList<string> CapturedNames,
		string? ReceiverBase);

	/// <summary>Clears per-function delegate and lambda-capture state.</summary>
	public void Reset(FunctionDeclarationSyntax func)
	{
		_delegateProvenances.Clear();
		_delegateParams.Clear();
		lambdaCaptures.Reset(func);
	}

	/// <summary>Registers a safe-delegate parameter as non-escaping for the current function.</summary>
	public void TrackParameter(string name, TypeSymbol type)
	{
		if (type is DelegateTypeSymbol)
			_delegateParams.Add(name);
	}

	/// <summary>
	/// Records declaration-time provenance for a delegate-typed local after its initializer has
	/// already been checked by the enclosing safety traversal.
	/// </summary>
	public void TrackDeclaration(VariableDeclarationSyntax declaration, VariableSymbol symbol, SymbolTable scope)
	{
		if (symbol.Type is DelegateTypeSymbol && declaration.Initializer != null)
			_delegateProvenances[declaration.Name] = GetDelegateProvenance(declaration.Initializer, scope);
	}

	/// <summary>Applies safe-delegate escape rules to a value returned from the current function.</summary>
	public void ValidateReturn(ExpressionSyntax expression, SymbolTable scope)
	{
		if (IsDelegateValueExpr(expression, scope))
			CheckDelegateEscape(expression, scope, expression.Span, isReturn: true);
	}

	/// <summary>Delegates lambda capture-policy and body validation to the dedicated capture analyzer.</summary>
	public void ValidateLambda(LambdaExpressionSyntax lambda, SymbolTable scope)
	{
		lambdaCaptures.ValidateLambda(lambda, scope);
	}

	/// <summary>
	/// Applies delegate-specific assignment checks and provenance propagation after the right-hand
	/// side has been visited, preserving the original diagnostic ordering.
	/// </summary>
	public void ValidateAssignment(BinaryExpressionSyntax assignment, SymbolTable scope)
	{
		if (assignment.Operator != "=")
			return;

		lambdaCaptures.ValidateCapturedAssignment(assignment);

		// §13.3 store to longer-lived storage: a capturing/ref lambda or a bound method
		// may not be stored into long-lived (global or parameter-origin) storage.
		if (IsLongLivedStoreDestination(assignment.Left, scope) && IsDelegateValueExpr(assignment.Right, scope))
			CheckDelegateEscape(assignment.Right, scope, assignment.Span, isReturn: false);

		// Propagate delegate provenance through reassignment of a delegate-typed variable.
		if (_getBaseIdentifierName(assignment.Left) is { } lhsName
			&& scope.Lookup(lhsName) is VariableSymbol reSym
			&& reSym.Type is DelegateTypeSymbol)
		{
			_delegateProvenances[lhsName] = GetDelegateProvenance(assignment.Right, scope);
		}
	}

	/// <summary>
	/// Computes the delegate provenance (context) of a delegate value expression. Capturing/ref
	/// lambdas carry closure environments, while bound methods carry receiver context.
	/// </summary>
	private DelegateProvenance GetDelegateProvenance(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case LambdaExpressionSyntax lam:
				var captures = lambdaCaptures.ComputeCapturedNames(lam, scope);
				if (captures.Count == 0) return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
				return new DelegateProvenance(
					lam.CaptureMode == LambdaCaptureMode.Ref ? DelegateProvenanceKind.RefBorrow : DelegateProvenanceKind.Capturing,
					[.. captures], null);
			case MemberAccessExpressionSyntax ma:
				// A delegate field or method group bound to a receiver: context == &receiver.
				// Only classify as a bound delegate value when the accessed member is genuinely a
				// delegate-typed field or a method group; a plain member read (e.g. 's.Id' where
				// Id is an int field) is not a delegate value.
				var memberReceiverType = _resolveExpressionType(ma.Expression, scope);
				if (memberReceiverType is PointerTypeSymbol memberReceiverPtr)
					memberReceiverType = memberReceiverPtr.ReferencedType;
				if (memberReceiverType is StructTypeSymbol receiverStruct && receiverStruct.FindField(ma.MemberName)?.Type is DelegateTypeSymbol)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], _getBaseIdentifierName(ma.Expression));
				if (memberReceiverType is UnionTypeSymbol receiverUnion && receiverUnion.FindField(ma.MemberName)?.Type is DelegateTypeSymbol)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], _getBaseIdentifierName(ma.Expression));
				if (memberReceiverType is not null &&
					context.GetExtensionMethodCandidates(memberReceiverType, context.CurrentUnit, ma.MemberName).Count > 0)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], _getBaseIdentifierName(ma.Expression));
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
			case IdentifierExpressionSyntax id when scope.Lookup(id.Name) is VariableSymbol dv && dv.Type is not DelegateTypeSymbol:
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
			case IdentifierExpressionSyntax id when _delegateProvenances.TryGetValue(id.Name, out var prior):
				return prior;
			default:
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
		}
	}

	/// <summary>
	/// Enforces delegate escape rules at return and long-lived-store boundaries while preserving the
	/// existing diagnostic IDs, text, and ordering.
	/// </summary>
	private void CheckDelegateEscape(ExpressionSyntax expr, SymbolTable scope, TextSpan span, bool isReturn)
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

		var provenance = GetDelegateProvenance(expr, scope);
		switch (provenance.Kind)
		{
			case DelegateProvenanceKind.Capturing:
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					isReturn
						? $"Captured closure environment cannot escape: the lambda captures '{string.Join(", ", provenance.CapturedNames)}' and may not cross the return boundary."
						: $"Captured closure environment cannot escape: the lambda captures '{string.Join(", ", provenance.CapturedNames)}' and may not be stored into long-lived storage.",
					DiagnosticIds.ClosureEnvironmentEscapes);
				break;
			case DelegateProvenanceKind.RefBorrow:
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					isReturn
						? $"Reference lambda borrow cannot escape: the lambda borrows '{string.Join(", ", provenance.CapturedNames)}' and may not cross the return boundary."
						: $"Reference lambda borrow cannot escape: the lambda borrows '{string.Join(", ", provenance.CapturedNames)}' and may not be stored into long-lived storage.",
					DiagnosticIds.RefLambdaBorrowEscapes);
				break;
			case DelegateProvenanceKind.BoundMethod:
				var receiver = provenance.ReceiverBase;
				if (receiver is null) return;
				if (scope.Lookup(receiver) is VariableSymbol recvSym)
				{
					if (recvSym.IsGlobal) return; // global receiver outlives everything
					context.Diagnostics.Report(context.CurrentUnit!.Context, span,
						$"Bound method on receiver '{receiver}' cannot escape: the receiver does not outlive the delegate.",
						DiagnosticIds.BoundReceiverLifetimeEscapes);
				}
				break;
		}
	}

	/// <summary>Returns whether an expression denotes a safe-delegate value.</summary>
	private bool IsDelegateValueExpr(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			LambdaExpressionSyntax => true,
			MemberAccessExpressionSyntax ma =>
				GetDelegateProvenance(ma, scope).Kind == DelegateProvenanceKind.BoundMethod,
			IdentifierExpressionSyntax id => scope.Lookup(id.Name) is VariableSymbol { Type: DelegateTypeSymbol },
			_ => false,
		};
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
