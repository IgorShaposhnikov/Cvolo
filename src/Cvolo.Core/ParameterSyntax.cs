namespace Cvolo.Core;

public sealed class ParameterSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Parameter;

    public string Type { get; }
    public string Name { get; }

    public ParameterSyntax(TextSpan span, string type, string name) : base(span)
    {
        Type = type;
        Name = name;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => [];
}
