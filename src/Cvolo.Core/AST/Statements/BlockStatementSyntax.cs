using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class BlockStatementSyntax(TextSpan span, IReadOnlyList<SyntaxNode> statements) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.BlockStatement;

	public IReadOnlyList<SyntaxNode> Statements { get; } = statements;

	public override IEnumerable<SyntaxNode> GetChildren() => Statements;
}
