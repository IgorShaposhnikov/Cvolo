namespace Cvolo.Core;

public sealed class FunctionDeclarationSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.FunctionDeclaration;

    public string ReturnType { get; }
    public string Name { get; }
    public IReadOnlyList<ParameterSyntax> Parameters { get; }
    public BlockStatementSyntax Body { get; }

    public FunctionDeclarationSyntax(
        TextSpan span,
        string returnType,
        string name,
        IReadOnlyList<ParameterSyntax> parameters,
        BlockStatementSyntax body) : base(span)
    {
        ReturnType = returnType;
        Name = name;
        Parameters = parameters;
        Body = body;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        foreach (var p in Parameters) yield return p;
        yield return Body;
    }
}
