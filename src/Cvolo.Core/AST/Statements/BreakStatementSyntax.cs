using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class BreakStatementSyntax(TextSpan span, string? label = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.BreakStatement;
	public string? Label { get; } = label;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
