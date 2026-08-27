using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class FunctionDeclarationSyntax(
	TextSpan span,
	string returnType,
	string name,
	IReadOnlyList<string> genericParameters,
	IReadOnlyList<ParameterSyntax> parameters,
	BlockStatementSyntax body,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	SafetyTier? modifier = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.FunctionDeclaration;

	public string ReturnType { get; } = returnType;
	public string Name { get; } = name;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;
	public BlockStatementSyntax Body { get; } = body;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];
	public SafetyTier? Modifier { get; } = modifier;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		foreach (var p in Parameters) yield return p;
		yield return Body;
	}
}
