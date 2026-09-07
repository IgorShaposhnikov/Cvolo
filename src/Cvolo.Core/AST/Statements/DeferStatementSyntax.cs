using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class DeferStatementSyntax(TextSpan span, SyntaxNode body) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.DeferStatement;

	public SyntaxNode Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren() => [Body];
}