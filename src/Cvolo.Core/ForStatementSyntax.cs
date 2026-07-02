namespace Cvolo.Core;

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
