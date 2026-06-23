namespace Cvolo.Core;

public sealed class StructDeclarationSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.StructDeclaration;

    public string Name { get; }
    public IReadOnlyList<StructFieldSyntax> Fields { get; }

    public StructDeclarationSyntax(TextSpan span, string name, IReadOnlyList<StructFieldSyntax> fields) : base(span)
    {
        Name = name;
        Fields = fields;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Fields;
}

public sealed class StructFieldSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Parameter;

    public string Type { get; }
    public string Name { get; }

    public StructFieldSyntax(TextSpan span, string type, string name) : base(span)
    {
        Type = type;
        Name = name;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => [];
}
