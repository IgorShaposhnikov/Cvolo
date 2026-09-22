using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Coordinates safe-delegate capture, escape, and provenance collaborators at the existing safety
/// traversal hooks without introducing an additional AST pass.
/// </summary>
internal sealed class SafeDelegateAnalyzer(
	BindingContext context,
	LambdaCaptureAnalyzer lambdaCaptures,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	private readonly SafeDelegateProvenanceTracker _provenance = new(
		context,
		lambdaCaptures,
		resolveExpressionType,
		getBaseIdentifierName);
	private readonly SafeDelegateEscapeValidator _escapes = new(context, getBaseIdentifierName);

	/// <summary>Clears per-function delegate and lambda-capture state.</summary>
	public void Reset(FunctionDeclarationSyntax func)
	{
		_provenance.Reset();
		_escapes.Reset();
		lambdaCaptures.Reset(func);
	}

	/// <summary>Registers a safe-delegate parameter as non-escaping for the current function.</summary>
	public void TrackParameter(string name, TypeSymbol type)
	{
		_escapes.TrackParameter(name, type);
	}

	/// <summary>
	/// Records declaration-time provenance for a delegate-typed local after its initializer has
	/// already been checked by the enclosing safety traversal.
	/// </summary>
	public void TrackDeclaration(VariableDeclarationSyntax declaration, VariableSymbol symbol, SymbolTable scope)
	{
		_provenance.TrackDeclaration(declaration, symbol, scope);
	}

	/// <summary>Applies safe-delegate escape rules to a value returned from the current function.</summary>
	public void ValidateReturn(ExpressionSyntax expression, SymbolTable scope)
	{
		_escapes.ValidateReturn(expression, scope, _provenance);
	}

	/// <summary>Delegates lambda capture-policy and body validation to the dedicated capture analyzer.</summary>
	public void ValidateLambda(LambdaExpressionSyntax lambda, SymbolTable scope)
	{
		lambdaCaptures.ValidateLambda(lambda, scope);
	}

	/// <summary>
	/// Coordinates delegate-specific assignment checks and provenance propagation after the right-hand
	/// side has been visited, preserving the original diagnostic ordering.
	/// </summary>
	public void ValidateAssignment(BinaryExpressionSyntax assignment, SymbolTable scope)
	{
		if (assignment.Operator != "=")
			return;

		lambdaCaptures.ValidateCapturedAssignment(assignment);

		// §13.3 store to longer-lived storage: a capturing/ref lambda or a bound method
		// may not be stored into long-lived (global or parameter-origin) storage.
		_escapes.ValidateStore(assignment, scope, _provenance);

		// Propagate delegate provenance through reassignment of a delegate-typed variable.
		_provenance.TrackAssignment(assignment, scope);
	}
}
