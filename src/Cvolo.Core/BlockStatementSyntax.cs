namespace Cvolo.Core;

public sealed class BlockStatementSyntax(TextSpan span, IReadOnlyList<SyntaxNode> statements) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.BlockStatement;

	public IReadOnlyList<SyntaxNode> Statements { get; } = statements;

	public override IEnumerable<SyntaxNode> GetChildren() => Statements;
}
