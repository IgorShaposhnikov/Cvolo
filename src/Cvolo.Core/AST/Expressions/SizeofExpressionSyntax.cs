using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// `sizeof&lt;T&gt;()` — a compile-time operator that constant-folds to the complete
/// object size of `T` as a `nuint`, including internal and trailing padding.
/// </summary>
public sealed class SizeofExpressionSyntax(TextSpan span, string typeName) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.SizeOfExpression;

	public string TypeName { get; } = typeName;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield break;
	}
}
