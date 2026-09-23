using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// A delegate ABI block: 'unsafe "C" { internal delegate ... }'. This is a declaration block
/// (not an executable unsafe block): it groups native delegate declarations under a shared
/// calling convention. The block itself never produces runtime code.
/// </summary>
public sealed class DelegateBlockDeclarationSyntax(TextSpan span, string? callingConvention, IReadOnlyList<DelegateDeclarationSyntax> delegates) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.DelegateBlockDeclaration;

	/// <summary>
	/// The block calling convention ("C" or "system"); inherited by every member delegate.
	/// </summary>
	public string? CallingConvention { get; } = callingConvention;

	public IReadOnlyList<DelegateDeclarationSyntax> Delegates { get; } = delegates;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		return Delegates;
	}
}
