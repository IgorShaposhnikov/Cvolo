using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class VoidLiteralExpressionSyntax(TextSpan span) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.VoidLiteralExpression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield break;
	}
}
