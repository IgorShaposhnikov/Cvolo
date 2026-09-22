using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Validates lambda bodies as safe-callable bodies through the shared safety traversal and owns
/// the active capture-set stack used to reject mutation of captured values during body analysis.
/// </summary>
internal sealed class LambdaBodySafetyValidator(
	BindingContext context,
	UnsafeContextValidator unsafeContext,
	Func<ExpressionSyntax, string?> getBaseIdentifierName,
	Action<ExpressionSyntax, SymbolTable> checkExpressionSafety,
	Action<BlockStatementSyntax, SymbolTable, FunctionDeclarationSyntax> checkBlockSafety)
{
	/// <summary>The function whose body is currently being walked for lambda block bodies.</summary>
	private FunctionDeclarationSyntax? _enclosingFunc;

	/// <summary>Captured-variable sets of the lambdas currently being checked.</summary>
	private readonly Stack<HashSet<string>> _lambdaCaptureSets = [];
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;
	private readonly Action<ExpressionSyntax, SymbolTable> _checkExpressionSafety = checkExpressionSafety;
	private readonly Action<BlockStatementSyntax, SymbolTable, FunctionDeclarationSyntax> _checkBlockSafety = checkBlockSafety;

	/// <summary>Clears per-function lambda-body state and records the function that owns block bodies.</summary>
	public void Reset(FunctionDeclarationSyntax func)
	{
		_lambdaCaptureSets.Clear();
		_enclosingFunc = func;
	}

	/// <summary>
	/// Validates a lambda body in a safe tier after capture policy has been applied, preserving the
	/// existing parameter scope construction and recursive traversal order.
	/// </summary>
	public void Validate(LambdaExpressionSyntax lambda, SymbolTable scope, HashSet<string> capturedNames)
	{
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
}
