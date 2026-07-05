using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class StringLiteralExpressionSyntax(TextSpan span, string value) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.StringLiteralExpression;

	public string Value { get; } = value;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
