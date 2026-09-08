using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// An extern block: [LibraryImport("lib")] extern "C" { ... }
/// Groups multiple extern function declarations under a shared library and calling convention.
/// </summary>
public sealed class ExternBlockSyntax(
	TextSpan span,
	IReadOnlyList<AttributeSyntax> attributes,
	string? callingConvention,
	IReadOnlyList<ExternBlockFunctionSyntax> functions,
	Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExternBlock;

	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes;
	public string? CallingConvention { get; } = callingConvention;
	public IReadOnlyList<ExternBlockFunctionSyntax> Functions { get; } = functions;
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public override IEnumerable<SyntaxNode> GetChildren() => Functions.Cast<SyntaxNode>().Concat(Attributes.Cast<SyntaxNode>());
}
