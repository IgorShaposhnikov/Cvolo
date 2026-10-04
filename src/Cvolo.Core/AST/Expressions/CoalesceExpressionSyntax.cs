using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// Option fallback (coalescing) expression <c>left ?? right</c>.
/// </summary>
/// <remarks>
/// <para>
/// The left operand is always an <c>Option&lt;T&gt;</c>. When it is <c>Some</c> the result is its
/// payload; when it is <c>None</c> the right operand supplies the value. The right operand is
/// evaluated only on the <c>None</c> path, so its side effects, allocations and resource
/// acquisition are conditional.
/// </para>
/// <para>
/// The right operand may itself be a fallback expression; the operator is right-associative, which
/// the parser establishes by rotating <c>(a ?? b) ?? c</c> into <c>a ?? (b ?? c)</c> while building
/// the syntax tree.
/// </para>
/// </remarks>
public sealed class CoalesceExpressionSyntax(TextSpan span, ExpressionSyntax left, ExpressionSyntax right) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.CoalesceExpression;
	public ExpressionSyntax Left { get; } = left;
	public ExpressionSyntax Right { get; } = right;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Left;
		yield return Right;
	}
}
