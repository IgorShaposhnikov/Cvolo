using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class TryStatementSyntax(TextSpan span, BlockStatementSyntax body, IReadOnlyList<CatchClauseSyntax> catchClauses) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.TryStatement;

	public BlockStatementSyntax Body { get; } = body;

	public IReadOnlyList<CatchClauseSyntax> CatchClauses { get; } = catchClauses;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Body;
		foreach (var clause in CatchClauses) yield return clause;
	}
}
