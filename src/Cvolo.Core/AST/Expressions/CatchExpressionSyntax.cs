using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class CatchLambdaExpressionSyntax(TextSpan span, string errorName, BlockStatementSyntax body) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.CatchLambdaExpression;

	// The name bound to the caught error value inside the arrow body, e.g. `(fileErr)`.
	public string ErrorName { get; } = errorName;

	public BlockStatementSyntax Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren() => [Body];
}

public sealed class CatchExpressionSyntax(TextSpan span, ExpressionSyntax operand, ExpressionSyntax? fallback, CatchLambdaExpressionSyntax? lambda = null) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.CatchExpression;

	public ExpressionSyntax Operand { get; } = operand;

	// The fallback expression for the literal form (`X catch F`); null when a lambda is used.
	public ExpressionSyntax? Fallback { get; } = fallback;

	// The arrow-lambda form (`X catch (e) => { ... }`); null when a literal fallback is used.
	public CatchLambdaExpressionSyntax? Lambda { get; } = lambda;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Operand;
		if (Fallback is not null) yield return Fallback;
		if (Lambda is not null) yield return Lambda;
	}
}
