namespace Cvolo.Core;

public sealed class VariableDeclarationSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.VariableDeclaration;

    public bool IsMutable { get; }
    public string? Type { get; }
    public string Name { get; }
    public ExpressionSyntax? Initializer { get; }

    public VariableDeclarationSyntax(
        TextSpan span,
        bool isMutable,
        string? type,
        string name,
        ExpressionSyntax? initializer) : base(span)
    {
        IsMutable = isMutable;
        Type = type;
        Name = name;
        Initializer = initializer;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        if (Initializer is not null) yield return Initializer;
    }
}
