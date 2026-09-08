using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class AsmOperandSyntax(TextSpan span, string? name, string constraint, ExpressionSyntax expression) : SyntaxNode(span)
{
    public string? Name { get; } = name;

    public string Constraint { get; } = constraint;

    public ExpressionSyntax Expression { get; } = expression;

    public bool IsOutput => Constraint.StartsWith('=');

    public override SyntaxKind Kind => SyntaxKind.AsmOperand;

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Expression;
    }
}
