using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class ForStatementSyntax(
	TextSpan span,
	VariableDeclarationSyntax initializer,
	ExpressionSyntax condition,
	ExpressionSyntax increment,
	SyntaxNode body) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ForStatement;

	public VariableDeclarationSyntax Initializer { get; } = initializer;
	public ExpressionSyntax Condition { get; } = condition;
	public ExpressionSyntax Increment { get; } = increment;
	public SyntaxNode Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Initializer;
		yield return Condition;
		yield return Increment;
		yield return Body;
	}
}
