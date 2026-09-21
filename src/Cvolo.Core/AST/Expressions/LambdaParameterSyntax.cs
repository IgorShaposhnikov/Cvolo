using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class LambdaParameterSyntax(TextSpan span, string name, string? explicitType = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.LambdaParameter;

	public string Name { get; } = name;
	public string? ExplicitType { get; } = explicitType;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}