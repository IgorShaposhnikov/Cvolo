using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class ArrayReplicationExpressionSyntax(TextSpan span, ExpressionSyntax value, ExpressionSyntax count) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.ArrayReplicationExpression;

	public ExpressionSyntax Value { get; } = value;
	public ExpressionSyntax Count { get; } = count;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Value;
		yield return Count;
	}
}
