namespace Cvolo.Core;

public sealed class CompilationUnitSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.CompilationUnit;

    public IReadOnlyList<SyntaxNode> Members { get; }

    public CompilationUnitSyntax(TextSpan span, IReadOnlyList<SyntaxNode> members) : base(span)
    {
        Members = members;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Members;
}
