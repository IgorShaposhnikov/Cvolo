using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class VariableDeclarationSyntax(
	TextSpan span,
	bool isMutable,
	string? type,
	string name,
	ExpressionSyntax? initializer,
	bool isExplicitVal = false) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.VariableDeclaration;

	public bool IsMutable { get; } = isMutable;
	/// <summary>
	/// True for an explicit `val` qualifier; bare typed declarations retain legacy member-write behavior.
	/// </summary>
	public bool IsExplicitVal { get; } = isExplicitVal;
	public string? Type { get; } = type;
	public string Name { get; } = name;
	public ExpressionSyntax? Initializer { get; } = initializer;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		if (Initializer is not null) yield return Initializer;
	}
}
