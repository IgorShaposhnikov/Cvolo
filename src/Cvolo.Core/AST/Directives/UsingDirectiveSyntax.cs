using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Directives;

public sealed class UsingDirectiveSyntax(
	TextSpan span,
	string namespaceName,
	bool isExposed = false,
	string? aliasName = null,
	TextSpan? aliasSpan = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.UsingDirective;
	public string NamespaceName { get; } = namespaceName;
	public bool IsExposed { get; } = isExposed;

	/// <summary>
	/// Local alias identifier for the <c>using Alias = Namespace.Path;</c> form, or null for an
	/// ordinary namespace import.
	/// </summary>
	public string? AliasName { get; } = aliasName;

	/// <summary>Span of <see cref="AliasName"/> so tooling can locate the alias identifier itself.</summary>
	public TextSpan? AliasSpan { get; } = aliasSpan;

	/// <summary>
	/// Whether this directive declares a namespace alias instead of importing namespace members.
	/// An alias only binds a namespace prefix; it never makes unqualified members visible.
	/// </summary>
	public bool IsAlias => AliasName is not null;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}