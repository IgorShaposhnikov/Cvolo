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
	IReadOnlyDictionary<string, string>? genericParameterDefaults = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.UnionDeclaration;

	public string Name { get; } = name;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<UnionFieldSyntax> Fields { get; } = fields;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public IReadOnlyDictionary<string, string> GenericParameterDefaults { get; } = genericParameterDefaults ?? new Dictionary<string, string>();

	public override IEnumerable<SyntaxNode> GetChildren() => Fields;
}

public sealed class UnionFieldSyntax(TextSpan span, string type, string name, Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.UnionField;

	public string Type { get; } = type;
	public string Name { get; } = name;

	public bool IsVoidVariant => Type == "void";

	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
