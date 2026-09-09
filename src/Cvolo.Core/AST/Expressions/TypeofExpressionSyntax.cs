using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// `typeof(type)` — a compile-time operator that constant-folds to a
/// `System.Type` value (a struct constant with an `id` and `name`).
/// </summary>
public sealed class TypeofExpressionSyntax(TextSpan span, string typeName) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.TypeOfExpression;

	public string TypeName { get; } = typeName;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield break;
	}
}
