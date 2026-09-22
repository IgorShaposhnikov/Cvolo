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
/// Owns lambda capture discovery and capture-policy enforcement, including move/ref capture
/// transitions and safe-tier validation of lambda bodies through the shared safety traversal.
/// </summary>
internal sealed class LambdaCaptureAnalyzer(
	BindingContext context,
	BorrowTracker borrows,
	MoveAnalyzer moves,
	UnsafeContextValidator unsafeContext,
	Func<ExpressionSyntax, string?> getBaseIdentifierName,
	Action<ExpressionSyntax, SymbolTable> checkExpressionSafety,
	Action<BlockStatementSyntax, SymbolTable, FunctionDeclarationSyntax> checkBlockSafety)
{
	/// <summary>The function whose body is currently being walked for lambda block bodies.</summary>
	private FunctionDeclarationSyntax? _enclosingFunc;

	/// <summary>Variables holding an active immutable ref-lambda borrow.</summary>
	private readonly HashSet<string> _refCapturedVars = [];

	/// <summary>Captured-variable sets of the lambdas currently being checked.</summary>
	private readonly Stack<HashSet<string>> _lambdaCaptureSets = [];
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;
	private readonly Action<ExpressionSyntax, SymbolTable> _checkExpressionSafety = checkExpressionSafety;
	private readonly Action<BlockStatementSyntax, SymbolTable, FunctionDeclarationSyntax> _checkBlockSafety = checkBlockSafety;

	/// <summary>Clears per-function lambda-capture state and records the function that owns lambda bodies.</summary>
	public void Reset(FunctionDeclarationSyntax func)
	{
		_refCapturedVars.Clear();
		_lambdaCaptureSets.Clear();
		_enclosingFunc = func;
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
	/// Enforces the existing prohibition on assigning to captured variables while a lambda body
	/// is being checked, preserving the original diagnostic text and ordering.
	/// </summary>
	public void ValidateCapturedAssignment(BinaryExpressionSyntax assignment)
	{
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
	}

	/// <summary>
	/// Computes the set of by-value outer locals and parameters referenced by a lambda body.
	/// References to globals or to identities shadowed inside the body are not captures.
	/// </summary>
	public HashSet<string> ComputeCapturedNames(LambdaExpressionSyntax lambda, SymbolTable scope)
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
