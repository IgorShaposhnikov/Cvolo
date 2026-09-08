using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// A floating-point literal. <see cref="IsFloat"/> is true when the `f`/`F`
/// suffix was present (otherwise the literal is a double).
/// </summary>
public sealed class DoubleLiteralExpressionSyntax(TextSpan span, double value, bool isFloat = false) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.DoubleLiteralExpression;

	public double Value { get; } = value;

	public bool IsFloat { get; } = isFloat;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
