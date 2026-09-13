using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

/// <summary>
/// A single <c>catch</c> clause of a <c>try</c> statement. Exactly one clause shape applies:
/// a value-pattern (<c>ErrorCodes.NotFound</c>), a type-pattern (<c>FileError</c> or
/// <c>FileError fe</c>), or a bare fallback (<c>catch { }</c>).
/// </summary>
public sealed class CatchClauseSyntax(
	TextSpan span,
	string? errorTypeName,
	string? variantName,
	string? bindingName,
	bool isBare,
	BlockStatementSyntax body) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.CatchClause;

	/// <summary>The error type named by the pattern (e.g. "ErrorCodes", "FileError"); null for a bare clause.</summary>
	public string? ErrorTypeName { get; } = errorTypeName;

	/// <summary>The enum variant for a value-pattern (e.g. "NotFound"); null otherwise.</summary>
	public string? VariantName { get; } = variantName;

	/// <summary>The binding variable name for a type-pattern with binding (e.g. "fe"); null otherwise.</summary>
	public string? BindingName { get; } = bindingName;

	/// <summary>True for the bare <c>catch { }</c> clause matching any unmatched error.</summary>
	public bool IsBare { get; } = isBare;

	public BlockStatementSyntax Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren() => [Body];
}
