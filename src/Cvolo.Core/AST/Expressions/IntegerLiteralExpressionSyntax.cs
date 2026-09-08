using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// An integer literal. <see cref="Value"/> always holds the magnitude; the
/// result is sized by <see cref="LiteralType"/> ("uint"/"long"/"ulong"), or
/// inferred from range when it is null.
/// </summary>
public sealed class IntegerLiteralExpressionSyntax(TextSpan span, ulong value, string? literalType = null) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.IntegerLiteralExpression;

	public ulong Value { get; } = value;

	public string? LiteralType { get; } = literalType;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
