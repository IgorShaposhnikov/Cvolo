namespace Cvolo.Core;

public sealed class MemberInitializerSyntax(TextSpan span, string memberName, ExpressionSyntax expression) : SyntaxNode(span)
{
    public override SyntaxKind Kind => SyntaxKind.Parameter;
    public string MemberName { get; } = memberName;
    public ExpressionSyntax Expression { get; } = expression;

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Expression;
    }
}

public sealed class StructInitializationExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.StructInitializationExpression;
    public string StructTypeName { get; }
    public IReadOnlyList<MemberInitializerSyntax> Initializers { get; }

    public StructInitializationExpressionSyntax(
        TextSpan span,
        string structTypeName,
        IReadOnlyList<MemberInitializerSyntax> initializers) : base(span)
    {
        StructTypeName = structTypeName;
        Initializers = initializers;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Initializers;
}