namespace Cvolo.Core;

public sealed class ForStatementSyntax : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.ForStatement;

    public VariableDeclarationSyntax Initializer { get; }
    public ExpressionSyntax Condition { get; }
    public ExpressionSyntax Increment { get; }
    public SyntaxNode Body { get; }

    public ForStatementSyntax(
        TextSpan span,
        VariableDeclarationSyntax initializer,
        ExpressionSyntax condition,
        ExpressionSyntax increment,
        SyntaxNode body) : base(span)
    {
        Initializer = initializer;
        Condition = condition;
        Increment = increment;
        Body = body;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Initializer;
        yield return Condition;
        yield return Increment;
        yield return Body;
    }
}
