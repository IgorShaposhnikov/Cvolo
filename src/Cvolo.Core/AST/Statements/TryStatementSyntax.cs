using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class TryStatementSyntax(TextSpan span, BlockStatementSyntax body, IReadOnlyList<CatchClauseSyntax> catchClauses, BlockStatementSyntax? finallyBody = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.TryStatement;

	public BlockStatementSyntax Body { get; } = body;

	public IReadOnlyList<CatchClauseSyntax> CatchClauses { get; } = catchClauses;

	/// <summary>The optional <c>finally</c> block, which runs on every exit from the try/catch.</summary>
	public BlockStatementSyntax? FinallyBody { get; } = finallyBody;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Body;
		foreach (var clause in CatchClauses) yield return clause;
		if (FinallyBody is not null) yield return FinallyBody;
	}
}
