using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class ReturnStatementSyntax(TextSpan span, ExpressionSyntax? expression) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ReturnStatement;

	public ExpressionSyntax? Expression { get; } = expression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		if (Expression is not null) yield return Expression;
	}
}
