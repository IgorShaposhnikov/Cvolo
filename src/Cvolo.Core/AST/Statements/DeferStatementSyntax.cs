using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class DeferStatementSyntax(TextSpan span, SyntaxNode body, string? label = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.DeferStatement;

	// Non-null for a targeted defer (`defer label: body;`): the body is anchored to the
	// labeled loop's body scope rather than the immediately enclosing block.
	public string? Label { get; } = label;

	public SyntaxNode Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren() => [Body];
}
