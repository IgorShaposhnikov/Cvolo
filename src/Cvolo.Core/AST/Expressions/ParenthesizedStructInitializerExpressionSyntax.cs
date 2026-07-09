using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class ParenthesizedStructInitializerExpressionSyntax(TextSpan span, IReadOnlyList<MemberInitializerSyntax> initializers) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.ParenthesizedStructInitializerExpression;

	public IReadOnlyList<MemberInitializerSyntax> Initializers { get; } = initializers;

	// Populated dynamically during compile-time ValidationPass
	public string? ResolvedStructTypeName { get; set; }

	public override IEnumerable<SyntaxNode> GetChildren() => Initializers;
}
