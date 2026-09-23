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
	Visibility? visibility = null,
	IReadOnlyList<GlobalVariableDeclarationSyntax>? globals = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ExternBlock;

	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes;
	public string? CallingConvention { get; } = callingConvention;
	public IReadOnlyList<ExternBlockFunctionSyntax> Functions { get; } = functions;

	/// <summary>
	/// Imported foreign globals declared inside the block ('global var T N;' / 'global T N;').
	/// They reference external mutable data and are read-only at the source level.
	/// </summary>
	public IReadOnlyList<GlobalVariableDeclarationSyntax> Globals { get; } = globals ?? [];

	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public override IEnumerable<SyntaxNode> GetChildren() =>
		Functions.Cast<SyntaxNode>().Concat(Globals.Cast<SyntaxNode>()).Concat(Attributes.Cast<SyntaxNode>());
}
