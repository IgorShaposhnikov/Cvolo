using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class HeapArrayAllocationExpressionSyntax(TextSpan span, string elementTypeName, ExpressionSyntax countExpression) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.HeapArrayAllocationExpression;

	public string ElementTypeName { get; } = elementTypeName;
	public ExpressionSyntax CountExpression { get; } = countExpression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return CountExpression;
	}
}
