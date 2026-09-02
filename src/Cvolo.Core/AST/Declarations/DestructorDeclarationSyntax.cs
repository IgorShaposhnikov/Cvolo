using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class DestructorDeclarationSyntax(TextSpan span, string structName, BlockStatementSyntax body, IReadOnlyList<AttributeSyntax>? attributes = null, Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.DestructorDeclaration;

	public string StructName { get; } = structName;
	public BlockStatementSyntax Body { get; } = body;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;
	public Visibility? SyntacticVisibility { get; } = visibility;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Body;
	}

	/// <summary>
	/// Destructors flow through binding, validation and emission as ordinary void
	/// extension methods named "~T", so all existing machinery applies unchanged.
	/// The body reference is shared, keeping diagnostics spans accurate.
	/// </summary>
	public FunctionDeclarationSyntax ToFunctionDeclaration()
		=> new(Span, "void", $"~{StructName}", [], [], Body, Attributes, visibility: SyntacticVisibility);
}
