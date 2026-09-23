using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class UnionDeclarationSyntax(
	TextSpan span,
	string name,
	IReadOnlyList<string> genericParameters,
	IReadOnlyList<UnionFieldSyntax> fields,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	Visibility? visibility = null,
	IReadOnlyDictionary<string, string>? genericParameterDefaults = null,
	bool isUnsafe = false) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.UnionDeclaration;

	public string Name { get; } = name;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<UnionFieldSyntax> Fields { get; } = fields;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	/// <summary>
	/// True when the union is declared with the 'unsafe' modifier: a raw C-compatible union with
	/// overlapping storage and no tag, as opposed to the default tagged union.
	/// </summary>
	public bool IsUnsafe { get; } = isUnsafe;

	public IReadOnlyDictionary<string, string> GenericParameterDefaults { get; } = genericParameterDefaults ?? new Dictionary<string, string>();

	public override IEnumerable<SyntaxNode> GetChildren() => Fields;
}

public sealed class UnionFieldSyntax(TextSpan span, string type, string name, Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.UnionField;

	public string Type { get; } = type;
	public string Name { get; } = name;

	public bool IsVoidVariant => Type == "void";

	/// <summary>Visibility explicitly written on the field, or null when it inherits the union visibility.</summary>
	public Visibility? SyntacticVisibility { get; } = visibility;

	/// <summary>Standalone fallback used before the enclosing union applies inherited visibility.</summary>
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}