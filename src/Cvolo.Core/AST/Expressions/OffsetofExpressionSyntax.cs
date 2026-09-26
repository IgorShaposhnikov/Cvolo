using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// A single segment of an `offsetof` member designator, retaining its source span so
/// diagnostics, navigation and rename can target the exact member name.
/// </summary>
public sealed record OffsetofMemberSyntax(string Name, TextSpan Span);

/// <summary>
/// `offsetof&lt;T&gt;(member)` — a compile-time operator that constant-folds to the byte
/// offset of a stored member (possibly a nested path) as a `nuint`.
/// </summary>
public sealed class OffsetofExpressionSyntax(
	TextSpan span,
	string typeName,
	IReadOnlyList<OffsetofMemberSyntax> members) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.OffsetOfExpression;

	public string TypeName { get; } = typeName;

	public IReadOnlyList<OffsetofMemberSyntax> Members { get; } = members;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield break;
	}
}
