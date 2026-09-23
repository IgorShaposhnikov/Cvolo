using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class GlobalVariableDeclarationSyntax(
	TextSpan span,
	string type,
	string name,
	ExpressionSyntax? initializer,
	bool isMutable = false,
	Visibility? visibility = null,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	bool isForeign = false,
	string? callingConvention = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.GlobalVariableDeclaration;

	public string Type { get; } = type;
	public string Name { get; } = name;
	public ExpressionSyntax? Initializer { get; } = initializer;
	public bool IsMutable { get; } = isMutable;
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];

	/// <summary>
	/// True when this global references externally defined data (an imported foreign global),
	/// either from an extern block ('extern "C" { global var T N; }') or the standalone form
	/// ('extern "C" global var T N;'). Such globals are read-only at the source level but are
	/// NOT LLVM constants: they resolve to external mutable data symbols.
	/// </summary>
	public bool IsForeign { get; } = isForeign;

	/// <summary>
	/// The calling convention of the extern block or standalone declaration that owns a foreign
	/// global ("C" or "system"). Null for ordinary Cvolo globals.
	/// </summary>
	public string? CallingConvention { get; } = callingConvention;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		if (Initializer is not null)
			yield return Initializer;
	}
}
