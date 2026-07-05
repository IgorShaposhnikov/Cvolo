using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class BooleanLiteralExpressionSyntax(TextSpan span, bool value) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.BooleanLiteralExpression;

	public bool Value { get; } = value;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
