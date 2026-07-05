using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class ParameterSyntax(TextSpan span, string type, string name) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.Parameter;

	public string Type { get; } = type;
	public string Name { get; } = name;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
