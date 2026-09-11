using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class ForEachStatementSyntax(
	TextSpan span,
	ForEachVariableKind bindingKind,
	string? explicitItemType,
	string itemName,
	ExpressionSyntax collection,
	BlockStatementSyntax body,
	string? label = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.ForEachStatement;

	public ForEachVariableKind BindingKind { get; } = bindingKind;
	public string? ExplicitItemType { get; } = explicitItemType;
	public string ItemName { get; } = itemName;
	public ExpressionSyntax Collection { get; } = collection;
	public BlockStatementSyntax Body { get; } = body;
	public string? Label { get; } = label;

	// Populated by the binder (ValidationPass) for emitter consumption.
	/// <summary>The value-level item type (the element type for arrays/slices; the
	/// unwrapped <c>T</c> of <c>Current</c> for enumerators).</summary>
	public string? ItemTypeName { get; set; }

	/// <summary>The declared item binding type as a string. For reference bindings this is
	/// <c>refvar T</c>/<c>ref T</c>; for value bindings it equals <see cref="ItemTypeName"/>.</summary>
	public string? ItemBindingTypeName { get; set; }

	/// <summary>True when the loop variable is a reference binding (pointer slot) rather
	/// than a detached value copy.</summary>
	public bool IsReferenceBinding { get; set; }

	/// <summary>True when 'Current' returns a reference (<c>ref T</c> or <c>refvar T</c>)
	/// rather than a value; false for arrays/slices and by-value enumerators.</summary>
	public bool CurrentReturnsReference { get; set; }

	public string? GetEnumeratorFunctionName { get; set; }
	public string? MoveNextFunctionName { get; set; }
	public string? CurrentFunctionName { get; set; }
	public string? EnumeratorTypeName { get; set; }

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Collection;
		yield return Body;
	}
}
