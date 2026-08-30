using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class IntegerLiteralExpressionSyntax(TextSpan span, long value) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.IntegerLiteralExpression;

	public long Value { get; } = value;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
