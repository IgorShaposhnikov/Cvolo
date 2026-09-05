using System.Reflection.Metadata;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class StructDeclarationSyntax(
	TextSpan span,
	string name,
	IReadOnlyList<string> genericParameters,
	IReadOnlyList<StructFieldSyntax> fields,
	string? embeddedType = null,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	Visibility? visibility = null,
	IReadOnlyDictionary<string, string>? genericParameterDefaults = null,
	IReadOnlyDictionary<string, List<string>>? genericParameterConstraints = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.StructDeclaration;

	public string Name { get; } = name;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<StructFieldSyntax> Fields { get; } = fields;

	/// <summary>The struct embedded via the `embed Base` header clause, if any.
	/// Embedded fields flatten at the beginning of this struct's layout and the
	/// embedded type's extension methods are promoted onto this struct.</summary>
	public string? EmbeddedType { get; } = embeddedType;

	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];

	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;

	// for interface/protocol bounds(e.g., "A" -> "IAllocator")
	public IReadOnlyDictionary<string, List<string>> GenericParameterConstraints { get; } = genericParameterConstraints ?? new Dictionary<string, List<string>>();
	public IReadOnlyDictionary<string, string> GenericParameterDefaults { get; } = genericParameterDefaults ?? new Dictionary<string, string>();

	public override IEnumerable<SyntaxNode> GetChildren() => Fields;
}

public sealed class StructFieldSyntax(TextSpan span, string type, string name, Visibility? visibility = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.Parameter;

	public string Type { get; } = type;
	public string Name { get; } = name;

	public Visibility Visibility { get; } = visibility ?? Visibility.Private;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
