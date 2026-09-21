using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Enforces the safety contract for <c>foreach</c> loops, including reference-item
/// escape boundaries and the immutable topology of the iterated collection.
/// </summary>
internal sealed class ForEachSafetyValidator(
	BindingContext context,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	/// <summary>
	/// Validates a foreach body before ordinary statement safety analysis. Reference loop
	/// variables may not escape the loop block, and the collection itself may not be
	/// structurally mutated while iteration is active.
	/// </summary>
	public void Validate(ForEachStatementSyntax statement, BlockStatementSyntax body)
	{
		var itemName = statement.ItemName;
		var collectionBase = getBaseIdentifierName(statement.Collection);

		// Targets declared inside the loop body cannot outlive that body. The loop item itself
		// is also safe because writes through it target only the current element slot.
		var safeTargets = new HashSet<string> { itemName };
		CollectLocalDeclarationNames(body, safeTargets);

		if (statement.IsReferenceBinding)
		{
			ValidateReferenceItemReturns(body, itemName);
			ValidateReferenceItemStores(body, itemName, safeTargets);
			ValidateReferenceItemArguments(body, itemName);
		}

		if (collectionBase is not null)
			ValidateCollectionImmutability(body, collectionBase);
	}

	/// <summary>
	/// Rejects returns that carry the synthetic foreach reference outside its lexical block.
	/// </summary>
	private void ValidateReferenceItemReturns(BlockStatementSyntax body, string itemName)
	{
		foreach (var ret in EnumerateNodes<ReturnStatementSyntax>(body))
		{
			if (ret.Expression is not null && BorrowTracker.ExpressionContainsRefUse(ret.Expression, itemName))
			{
				ReportReferenceEscape(ret.Span, itemName);
			}
		}
	}

	/// <summary>
	/// Rejects assignments that store the foreach reference into a location that outlives
	/// the loop block.
	/// </summary>
	private void ValidateReferenceItemStores(
		BlockStatementSyntax body,
		string itemName,
		IReadOnlySet<string> safeTargets)
	{
		foreach (var assign in EnumerateNodes<BinaryExpressionSyntax>(body))
		{
			if (assign.Operator != "=")
				continue;

			var lhsBase = getBaseIdentifierName(assign.Left);
			if (lhsBase is not null &&
				!safeTargets.Contains(lhsBase) &&
				BorrowTracker.ExpressionContainsRefUse(assign.Right, itemName))
			{
				ReportReferenceEscape(assign.Span, itemName);
			}
		}
	}

	/// <summary>
	/// Rejects passing the synthetic foreach reference to a reference parameter because the
	/// callee-side lifetime cannot be proven to remain inside the loop body.
	/// </summary>
	private void ValidateReferenceItemArguments(BlockStatementSyntax body, string itemName)
	{
		foreach (var call in EnumerateNodes<CallExpressionSyntax>(body))
		{
			if (!context.ResolvedCalls.TryGetValue(call, out var callee))
				continue;

			for (var i = 0; i < call.Arguments.Count && i < callee.Parameters.Count; i++)
			{
				if (callee.Parameters[i].Type is PointerTypeSymbol &&
					BorrowTracker.ExpressionContainsRefUse(call.Arguments[i], itemName))
				{
					ReportReferenceEscape(call.Arguments[i].Span, itemName);
				}
			}
		}
	}

	/// <summary>
	/// Enforces the immutable structural-borrow contract on the collection identifier while
	/// its foreach body executes.
	/// </summary>
	private void ValidateCollectionImmutability(BlockStatementSyntax body, string collectionBase)
	{
		foreach (var assign in EnumerateNodes<BinaryExpressionSyntax>(body))
		{
			if (assign.Operator == "=" && getBaseIdentifierName(assign.Left) == collectionBase)
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, assign.Span,
					$"'{collectionBase}' is under an immutable borrow contract while it is being iterated: structural mutation is not allowed inside the 'foreach' body.");
			}
		}

		foreach (var call in EnumerateNodes<CallExpressionSyntax>(body))
		{
			var dot = call.FunctionName.IndexOf('.');
			if (dot > 0 && call.FunctionName.AsSpan(0, dot).SequenceEqual(collectionBase))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, call.Span,
					$"'{collectionBase}' is under an immutable borrow contract while it is being iterated: mutating method calls are not allowed inside the 'foreach' body.");
			}
		}
	}

	/// <summary>
	/// Adds every local declaration in the body subtree to the set of destinations whose
	/// lifetime is bounded by the foreach block.
	/// </summary>
	private static void CollectLocalDeclarationNames(SyntaxNode root, HashSet<string> destinationNames)
	{
		if (root is VariableDeclarationSyntax declaration)
			destinationNames.Add(declaration.Name);

		foreach (var child in root.GetChildren())
			CollectLocalDeclarationNames(child, destinationNames);
	}

	/// <summary>
	/// Enumerates syntax nodes of a requested type in depth-first order within the loop body.
	/// </summary>
	private static IEnumerable<TSyntax> EnumerateNodes<TSyntax>(SyntaxNode root)
		where TSyntax : SyntaxNode
	{
		if (root is TSyntax match)
			yield return match;

		foreach (var child in root.GetChildren())
		{
			foreach (var nested in EnumerateNodes<TSyntax>(child))
				yield return nested;
		}
	}

	/// <summary>
	/// Reports CVL1088 for a reference foreach item that crosses the lexical loop boundary.
	/// </summary>
	private void ReportReferenceEscape(TextSpan span, string itemName)
	{
		context.Diagnostics.Report(context.CurrentUnit!.Context, span,
			$"Escape Boundary Violation: reference loop variable '{itemName}' uses an internal stack provenance exception and cannot cross the lexical boundary of the loop block.",
			DiagnosticIds.ForeachEscapeBoundary);
	}
}
