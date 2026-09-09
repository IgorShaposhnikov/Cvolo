using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// A native binary export block: expose extern "C" { ... }.
/// Groups functions under a shared binary calling convention; each function is a normal
/// Cvolo function that additionally receives a synthesized exported symbol alias.
/// </summary>
public sealed class ExposeExternBlockSyntax(
	TextSpan span,
	IReadOnlyList<AttributeSyntax> attributes,
	string? callingConvention,
	IReadOnlyList<FunctionDeclarationSyntax> functions,
	Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExposeExternBlock;

	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes;
	public string? CallingConvention { get; } = callingConvention;
	public IReadOnlyList<FunctionDeclarationSyntax> Functions { get; } = functions;
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public override IEnumerable<SyntaxNode> GetChildren() => Functions.Cast<SyntaxNode>().Concat(Attributes.Cast<SyntaxNode>());
}
