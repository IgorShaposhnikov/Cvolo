using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class DefaultExpressionSyntax(TextSpan span, string? typeName) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.DefaultExpression;

	public string? TypeName { get; } = typeName;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield break;
	}
}
