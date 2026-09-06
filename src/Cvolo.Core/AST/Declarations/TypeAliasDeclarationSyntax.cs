using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// A top-level type alias declaration: <c>alias AliasName = ExistingType;</c>.
/// The alias is zero-cost — it is resolved to its underlying type during binding
/// and erased before code generation. Optional generic parameters enable
/// parameterized aliases (e.g. <c>alias NodeRef&lt;T&gt; = Mut&lt;LinkedListNode&lt;T&gt;&gt;?;</c>).
/// Attributes and visibility are reserved for a future module system.
/// </summary>
public sealed class TypeAliasDeclarationSyntax(
	TextSpan span,
	string name,
	string type,
	IReadOnlyList<string> genericParameters,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.TypeAliasDeclaration;

	public string Name { get; } = name;
	public string Type { get; } = type;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}