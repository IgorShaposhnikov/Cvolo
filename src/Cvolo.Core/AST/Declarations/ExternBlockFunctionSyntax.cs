using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// A single function declaration inside an extern block.
/// Carries its own attribute list (e.g. [ImportName("nativeName")]).
/// </summary>
public sealed class ExternBlockFunctionSyntax(
	TextSpan span,
	string returnType,
	string name,
	IReadOnlyList<ParameterSyntax> parameters,
	bool isVariadic,
	IReadOnlyList<AttributeSyntax> attributes) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExternBlockFunction;

	public string ReturnType { get; } = returnType;
	public string Name { get; } = name;
	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;
	public bool IsVariadic { get; } = isVariadic;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes;

	public override IEnumerable<SyntaxNode> GetChildren() => Parameters.Cast<SyntaxNode>().Concat(Attributes);
}
