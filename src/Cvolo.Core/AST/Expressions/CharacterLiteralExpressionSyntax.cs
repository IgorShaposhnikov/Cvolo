using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class CharacterLiteralExpressionSyntax(TextSpan span, char value) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.CharLiteralExpression;
	public char Value { get; } = value;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
