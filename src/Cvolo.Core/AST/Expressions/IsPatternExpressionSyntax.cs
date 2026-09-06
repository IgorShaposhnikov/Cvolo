using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// Union variant pattern-match test: <c>operand is Some value</c> or <c>operand is None</c>.
/// Produces a <c>bool</c>; when <paramref name="boundName"/> is present, the variant payload
/// is bound to that name in the enclosing scope.
/// </summary>
public sealed class IsPatternExpressionSyntax(TextSpan span, ExpressionSyntax operand, string variantName, string? boundName = null) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.IsPatternExpression;

	public ExpressionSyntax Operand { get; } = operand;
	public string VariantName { get; } = variantName;
	public string? BoundName { get; } = boundName;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Operand;
	}
}