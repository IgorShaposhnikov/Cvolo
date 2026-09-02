using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class GlobalVariableDeclarationSyntax(TextSpan span, string type, string name, ExpressionSyntax? initializer, bool isMutable = false, Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.GlobalVariableDeclaration;

	public string Type { get; } = type;
	public string Name { get; } = name;
	public ExpressionSyntax? Initializer { get; } = initializer;
	public bool IsMutable { get; } = isMutable;
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		if (Initializer is not null)
			yield return Initializer;
	}
}
