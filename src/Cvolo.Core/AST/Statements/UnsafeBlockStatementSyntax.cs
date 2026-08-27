using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class UnsafeBlockStatementSyntax(TextSpan span, BlockStatementSyntax body) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.UnsafeBlockStatement;

	public BlockStatementSyntax Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return body;
	}
}
