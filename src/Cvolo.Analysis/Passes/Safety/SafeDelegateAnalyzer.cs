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
/// Owns safe-delegate lambda capture policy, delegate provenance propagation, and escape
/// diagnostics while delegating recursive syntax traversal back to the shared safety traversal.
/// </summary>
internal sealed class SafeDelegateAnalyzer(
	BindingContext context,
	BorrowTracker borrows,
	MoveAnalyzer moves,
	UnsafeContextValidator unsafeContext,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName,
	Action<ExpressionSyntax, SymbolTable> checkExpressionSafety,
	Action<BlockStatementSyntax, SymbolTable, FunctionDeclarationSyntax> checkBlockSafety)
{
	/// <summary>The function whose body is currently being walked for lambda block bodies.</summary>
	private FunctionDeclarationSyntax? _enclosingFunc;

	/// <summary>
	/// Delegate provenance records for delegate-typed variables declared so far in the current
	/// function. Copy assignments propagate the carried closure or receiver context.
	/// </summary>
	private readonly Dictionary<string, DelegateProvenance> _delegateProvenances = [];

	/// <summary>Names of non-escaping safe-delegate parameters of the current function (§13.0).</summary>
	private readonly HashSet<string> _delegateParams = [];

	/// <summary>Names of delegate-typed locals/globals whose context must not escape.</summary>
	private readonly Dictionary<string, DelegateProvenance> _nonEscapingDelegates = [];

	/// <summary>Variables holding an active immutable ref-lambda borrow.</summary>
	private readonly HashSet<string> _refCapturedVars = [];

	/// <summary>Captured-variable sets of the lambdas currently being checked.</summary>
	private readonly Stack<HashSet<string>> _lambdaCaptureSets = [];
	private readonly Func<ExpressionSyntax, SymbolTable, TypeSymbol?> _resolveExpressionType = resolveExpressionType;
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;
	private readonly Action<ExpressionSyntax, SymbolTable> _checkExpressionSafety = checkExpressionSafety;
	private readonly Action<BlockStatementSyntax, SymbolTable, FunctionDeclarationSyntax> _checkBlockSafety = checkBlockSafety;

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

	/// <summary>Clears per-function delegate state and records the function that owns lambda bodies.</summary>
	public void Reset(FunctionDeclarationSyntax func)
	{
		_delegateProvenances.Clear();
		_delegateParams.Clear();
		_refCapturedVars.Clear();
		_lambdaCaptureSets.Clear();
		_enclosingFunc = func;
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

	/// <summary>
	/// Validates one lambda in-place, preserving the existing capture checks, move transitions,
	/// ref-capture locks, safe-tier body validation, and recursive traversal order.
	/// </summary>
	public void ValidateLambda(LambdaExpressionSyntax lambda, SymbolTable scope)
	{
		// §8.7 capture sources are outer locals + by-value parameters only; borrowed-ref
		// lexical bindings are never capture sources (§8.8). Compute the set from the
		// enclosing scope: any identifier in the body resolving to a non-global variable.
		var capturedNames = ComputeCapturedNames(lambda, scope);

		// Capture-policy validation (§8.2/8.3/8.5/8.7/8.9)
		foreach (var name in capturedNames)
		{
			if (scope.Lookup(name) is not VariableSymbol capturedSym)
				continue;

			if (capturedSym.Type is PointerTypeSymbol)
			{
				// §8.8 borrowed-ref lexical bindings are never capture sources in any mode.
				context.Diagnostics.Report(context.CurrentUnit!.Context, lambda.Span,
					$"Cannot capture reference binding '{name}' in a lambda; capture sources must be by-value locals and parameters.",
					DiagnosticIds.RefBindingCaptureUnsupported);
			}
			else if (DelegateTypeHelpers.ContainsMutableBorrowCapability(capturedSym.Type))
			{
				// §8.9 the captured value carries a mutable-borrow capability.
				context.Diagnostics.Report(context.CurrentUnit!.Context, lambda.Span,
					$"Cannot capture '{name}' of type '{capturedSym.Type.Name}': the type carries a mutable-borrow capability (refvar/slice/aggregate) which may not be captured.",
					DiagnosticIds.MutableBorrowCapabilityCapture);
			}
			else if (lambda.CaptureMode == LambdaCaptureMode.Default
					 && moves.IsMoveOnly(capturedSym.Type))
			{
				// §8.2 default mode copies a snapshot; move-only values cannot be copied.
				context.Diagnostics.Report(context.CurrentUnit!.Context, lambda.Span,
					$"Cannot capture '{name}' in default mode: '{capturedSym.Type.Name}' is move-only and cannot be copied; use 'move' or 'ref' capture.",
					DiagnosticIds.DefaultModeCaptureOfMoveOnly);
			}

			if (lambda.CaptureMode == LambdaCaptureMode.Move
				&& moves.IsMoveOnly(capturedSym.Type))
			{
				// §8.3 'move' capture moves the move-only source: it becomes unavailable.
				moves.MarkMoved(capturedSym);
			}

			if (lambda.CaptureMode == LambdaCaptureMode.Ref)
			{
				// §8.6 active immutable-borrow lock while the lambda may be invoked:
				// mutation, move, and incompatible mutable borrows of the captured
				// variable are blocked while the borrow is live.
				RegisterRefCaptureLock(name, lambda.Span);
			}
		}

		// Lambda bodies are validated as safe-callable bodies regardless of the
		// enclosing tier (§21.3). Check the body inside a child scope holding the
		// lambda parameters, pushing the capture set for captured-field checks.
		var lambdaScope = new SymbolTable(scope);
		if (context.ResolvedLambdas.TryGetValue(lambda, out var lamInfo))
		{
			for (var i = 0; i < lambda.Parameters.Count && i < lamInfo.ParameterTypes.Count; i++)
				lambdaScope.Declare(new VariableSymbol(lambda.Parameters[i].Name, lamInfo.ParameterTypes[i], false) { IsInitialized = true, Origin = OriginKind.Parameter });
		}
		else
		{
			foreach (var p in lambda.Parameters)
				lambdaScope.Declare(new VariableSymbol(p.Name, TypeSymbol.Int, false) { IsInitialized = true, Origin = OriginKind.Parameter });
		}

		_lambdaCaptureSets.Push(capturedNames);
		var savedTier = unsafeContext.PopOrSafe();
		unsafeContext.Push(SafetyTier.Safe);
		try
		{
			if (lambda.ExpressionBody != null)
				_checkExpressionSafety(lambda.ExpressionBody, lambdaScope);
			else if (lambda.BlockBody != null)
				_checkBlockSafety(lambda.BlockBody, lambdaScope, _enclosingFunc!);
		}
		finally
		{
			unsafeContext.Pop();
			if (savedTier != SafetyTier.Safe)
				unsafeContext.Push(savedTier);
			_lambdaCaptureSets.Pop();
		}
	}

	/// <summary>
	/// Applies delegate-specific assignment checks and provenance propagation after the right-hand
	/// side has been visited, preserving the original diagnostic ordering.
	/// </summary>
	public void ValidateAssignment(BinaryExpressionSyntax assignment, SymbolTable scope)
	{
		if (assignment.Operator != "=")
			return;

		// §8.2 captured variables (immutable snapshots / borrows) may not be assigned
		// or mutated while the enclosing lambda body is being checked (CVL1312/1313).
		if (_lambdaCaptureSets.Count > 0)
		{
			var lhsBase = _getBaseIdentifierName(assignment.Left);
			if (lhsBase is not null && _lambdaCaptureSets.Peek().Contains(lhsBase))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, assignment.Span,
					$"Cannot assign to captured variable '{lhsBase}': captured variables are immutable within the lambda body.",
					assignment.Left is MemberAccessExpressionSyntax
						? DiagnosticIds.CapturedFieldMutation
						: DiagnosticIds.CapturedFieldAssignment);
			}
		}

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
	/// Computes the set of by-value outer locals and parameters referenced by a lambda body.
	/// References to globals or to identities shadowed inside the body are not captures.
	/// </summary>
	private HashSet<string> ComputeCapturedNames(LambdaExpressionSyntax lambda, SymbolTable scope)
	{
		var captured = new HashSet<string>();
		var body = lambda.BlockBody is not null ? (SyntaxNode)lambda.BlockBody : lambda.ExpressionBody!;
		if (body is null)
			return captured;

		foreach (var id in EnumerateNodes<IdentifierExpressionSyntax>(body))
		{
			if (captured.Contains(id.Name))
				continue;
			if (scope.Lookup(id.Name) is VariableSymbol v && !v.IsGlobal)
				captured.Add(id.Name);
		}

		return captured;
	}

	/// <summary>
	/// Registers the immutable borrow lock held by a ref-capturing lambda for the lifetime of the
	/// resulting delegate value.
	/// </summary>
	private void RegisterRefCaptureLock(string name, TextSpan span)
	{
		var lockName = "$refλ:" + name;
		borrows.RegisterBorrow(lockName, name, false, span);
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
				var captures = ComputeCapturedNames(lam, scope);
				if (captures.Count == 0)
					return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
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

	/// <summary>Enumerates syntax nodes of the requested type using the existing recursive shape.</summary>
	private static IEnumerable<TSyntax> EnumerateNodes<TSyntax>(SyntaxNode root) where TSyntax : SyntaxNode
	{
		if (root is TSyntax match)
			yield return match;
		foreach (var child in root.GetChildren())
		{
			foreach (var nested in EnumerateNodes<TSyntax>(child))
			{
				yield return nested;
			}
		}
	}
}
