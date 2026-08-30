using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class EnumDeclarationSyntax(
	TextSpan span,
	string name,
	string? storageType,
	IReadOnlyList<EnumVariantDeclarationSyntax> variants,
	IReadOnlyList<AttributeSyntax>? attributes = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.EnumDeclaration;

	public string Name { get; } = name;
	public string? StorageType { get; } = storageType;
	public IReadOnlyList<EnumVariantDeclarationSyntax> Variants { get; } = variants;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];

	public override IEnumerable<SyntaxNode> GetChildren() => Variants;
}

public sealed class EnumVariantDeclarationSyntax(TextSpan span, string name, ExpressionSyntax? value = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.EnumVariant;

	public string Name { get; } = name;
	public ExpressionSyntax? Value { get; } = value;

	public override IEnumerable<SyntaxNode> GetChildren() => Value is not null ? [Value] : [];
}