namespace Cvolo.Core;

public sealed class CompilationUnitSyntax(TextSpan span, IReadOnlyList<SyntaxNode> members) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.CompilationUnit;

	public IReadOnlyList<SyntaxNode> Members { get; } = members;

	public override IEnumerable<SyntaxNode> GetChildren() => Members;
}
