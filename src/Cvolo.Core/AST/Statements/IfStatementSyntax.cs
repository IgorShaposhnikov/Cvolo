using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class IfStatementSyntax(
	TextSpan span,
	ExpressionSyntax condition,
	SyntaxNode thenStatement,
	ElseClauseSyntax? elseClause) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.IfStatement;

	public ExpressionSyntax Condition { get; } = condition;
	public SyntaxNode ThenStatement { get; } = thenStatement;
	public ElseClauseSyntax? ElseClause { get; } = elseClause;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Condition;
		yield return ThenStatement;
		if (ElseClause is not null)
			yield return ElseClause;
	}
}

public sealed class ElseClauseSyntax(TextSpan span, BlockStatementSyntax body) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.BlockStatement;

	public BlockStatementSyntax Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Body;
	}
}
