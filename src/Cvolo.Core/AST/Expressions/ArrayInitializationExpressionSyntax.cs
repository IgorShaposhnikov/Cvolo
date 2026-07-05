using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class ArrayInitializationExpressionSyntax(TextSpan span, IReadOnlyList<ExpressionSyntax> elements) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.ArrayInitializationExpression;
	public IReadOnlyList<ExpressionSyntax> Elements { get; } = elements;

	public override IEnumerable<SyntaxNode> GetChildren() => Elements;
}
