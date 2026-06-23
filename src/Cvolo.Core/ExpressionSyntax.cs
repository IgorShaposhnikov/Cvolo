namespace Cvolo.Core;

public abstract class ExpressionSyntax : SyntaxNode
{
    protected ExpressionSyntax(TextSpan span) : base(span) { }
}
