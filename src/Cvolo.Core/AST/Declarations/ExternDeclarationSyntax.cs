using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class ExternDeclarationSyntax(
	TextSpan span,
	string returnType,
	string name,
	IReadOnlyList<ParameterSyntax> parameters,
	bool isVariadic) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExternDeclaration;

	public string ReturnType { get; } = returnType;
	public string Name { get; } = name;
	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;
	public bool IsVariadic { get; } = isVariadic;

	public override IEnumerable<SyntaxNode> GetChildren() => Parameters;
}
