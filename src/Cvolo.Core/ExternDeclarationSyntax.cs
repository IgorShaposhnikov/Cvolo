namespace Cvolo.Core;

public sealed class ExternDeclarationSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.ExternDeclaration;

    public string ReturnType { get; }
    public string Name { get; }
    public IReadOnlyList<ParameterSyntax> Parameters { get; }
    public bool IsVariadic { get; }

    public ExternDeclarationSyntax(
        TextSpan span,
        string returnType,
        string name,
        IReadOnlyList<ParameterSyntax> parameters,
        bool isVariadic) : base(span)
    {
        ReturnType = returnType;
        Name = name;
        Parameters = parameters;
        IsVariadic = isVariadic;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Parameters;
}
