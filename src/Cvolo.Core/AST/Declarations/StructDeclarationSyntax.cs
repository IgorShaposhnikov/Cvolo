using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class StructDeclarationSyntax(TextSpan span, string name, IReadOnlyList<StructFieldSyntax> fields) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.StructDeclaration;

	public string Name { get; } = name;
	public IReadOnlyList<StructFieldSyntax> Fields { get; } = fields;

	public override IEnumerable<SyntaxNode> GetChildren() => Fields;
}

public sealed class StructFieldSyntax(TextSpan span, string type, string name) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.Parameter;

	public string Type { get; } = type;
	public string Name { get; } = name;

	public override IEnumerable<SyntaxNode> GetChildren() => [];
}
