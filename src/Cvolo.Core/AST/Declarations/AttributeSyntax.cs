using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class AttributeSyntax(TextSpan span, string name, IReadOnlyList<ExpressionSyntax> arguments) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.Attribute;

	public string Name { get; } = name;
	public IReadOnlyList<ExpressionSyntax> Arguments { get; } = arguments;

	public override IEnumerable<SyntaxNode> GetChildren() => Arguments;
}
