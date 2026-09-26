using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// `alignof&lt;T&gt;()` — a compile-time operator that constant-folds to the required
/// object-storage alignment of `T` as a `nuint` under the active target layout.
/// </summary>
public sealed class AlignofExpressionSyntax(TextSpan span, string typeName) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.AlignOfExpression;

	public string TypeName { get; } = typeName;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield break;
	}
}
