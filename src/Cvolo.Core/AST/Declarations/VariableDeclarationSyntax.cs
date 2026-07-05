using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class VariableDeclarationSyntax(
	TextSpan span,
	bool isMutable,
	string? type,
	string name,
	ExpressionSyntax? initializer) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.VariableDeclaration;

	public bool IsMutable { get; } = isMutable;
	public string? Type { get; } = type;
	public string Name { get; } = name;
	public ExpressionSyntax? Initializer { get; } = initializer;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		if (Initializer is not null) yield return Initializer;
	}
}
