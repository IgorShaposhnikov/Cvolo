namespace Cvolo.Core;

public sealed class CallExpressionSyntax : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.CallExpression;

    public string FunctionName { get; }
    public IReadOnlyList<ExpressionSyntax> Arguments { get; }

    public CallExpressionSyntax(
        TextSpan span,
        string functionName,
        IReadOnlyList<ExpressionSyntax> arguments) : base(span)
    {
        FunctionName = functionName;
        Arguments = arguments;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Arguments;
}
