using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class MemberInitializerSyntax(TextSpan span, string memberName, ExpressionSyntax expression) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.Parameter;
	public string MemberName { get; } = memberName;
	public ExpressionSyntax Expression { get; } = expression;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Expression;
	}
}

public sealed class StructInitializationExpressionSyntax(
	TextSpan span,
	string structTypeName,
	IReadOnlyList<MemberInitializerSyntax> initializers) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.StructInitializationExpression;
	public string StructTypeName { get; } = structTypeName;
	public IReadOnlyList<MemberInitializerSyntax> Initializers { get; } = initializers;

	public override IEnumerable<SyntaxNode> GetChildren() => Initializers;
}
