using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class NullLiteralExpressionSyntax(TextSpan span) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.NullLiteralExpression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield break;
	}
}
