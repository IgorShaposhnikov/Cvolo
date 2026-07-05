using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class WhileStatementSyntax(TextSpan span, ExpressionSyntax condition, SyntaxNode body) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.WhileStatement;

	public ExpressionSyntax Condition { get; } = condition;
	public SyntaxNode Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Condition;
		yield return Body;
	}
}
