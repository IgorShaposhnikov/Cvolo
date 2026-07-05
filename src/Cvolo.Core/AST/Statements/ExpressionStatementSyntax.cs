using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class ExpressionStatementSyntax(TextSpan span, ExpressionSyntax expression) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExpressionStatement;

	public ExpressionSyntax Expression { get; } = expression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Expression;
	}
}
