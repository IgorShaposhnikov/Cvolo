using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class ConstructorDeclarationSyntax(
	TextSpan span,
	string structName,
	IReadOnlyList<ParameterSyntax> parameters,
	BlockStatementSyntax body,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ConstructorDeclaration;

	public string StructName { get; } = structName;
	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;
	public BlockStatementSyntax Body { get; } = body;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;
	public Visibility? SyntacticVisibility { get; } = visibility;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		foreach (var p in Parameters) yield return p;
		yield return Body;
	}

	/// <summary>
	/// Constructors flow through validation and emission as ordinary void extension
	/// methods named after the struct ("T"), with the destination storage passed as
	/// the implicit "this" pointer at every call site. The body reference is shared,
	/// keeping diagnostics spans accurate.
	/// </summary>
	public FunctionDeclarationSyntax ToFunctionDeclaration() =>
		new(Span, "void", StructName, [], Parameters, Body, Attributes, visibility: SyntacticVisibility);
}
