using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// `nameof(expression)` — a compile-time operator that constant-folds to the
/// trailing, un-mangled name of the referenced symbol as a string literal.
/// </summary>
public sealed class NameofExpressionSyntax(TextSpan span, ExpressionSyntax argument) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.NameOfExpression;

	public ExpressionSyntax Argument { get; } = argument;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Argument;
	}
}
