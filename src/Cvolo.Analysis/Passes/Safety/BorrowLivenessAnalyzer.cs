using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Performs the syntax-use queries used for non-lexical borrow release and reference-escape
/// checks. It consumes the borrow state owned by <see cref="BorrowTracker"/> and does not own a
/// separate compiler traversal.
/// </summary>
internal sealed class BorrowLivenessAnalyzer(
	IReadOnlyDictionary<string, (string BorrowedName, bool IsMutable, int LastUseEnd, TextSpan DeclSpan)> activeRefs,
	Action<string> removeBorrower,
	Func<string, bool>? isProtectedBorrow = null)
{
	private readonly IReadOnlyDictionary<string, (string BorrowedName, bool IsMutable, int LastUseEnd, TextSpan DeclSpan)> _activeRefs = activeRefs;
	private readonly Action<string> _removeBorrower = removeBorrower;
	private readonly Func<string, bool> _isProtectedBorrow = isProtectedBorrow ?? (_ => false);

	/// <summary>
	/// Releases active references whose names are not used by the current or any later statement in
	/// the block. The statement-start argument is retained to preserve the previous call contract.
	/// </summary>
	public void ReleaseExpiredBorrows(int currentStatementStart, BlockStatementSyntax block, int startIndex)
	{
		_ = currentStatementStart;
		var stmts = block.Statements;
		var toRelease = new List<string>();

		foreach (var kv in _activeRefs)
		{
			var refName = kv.Key;
			// A borrowed name defined in an enclosing block may still be used
			// after this block, or at the start of another loop iteration.
			// This local scan cannot prove otherwise: defer its release to
			// the enclosing block's own NLL scan.
			if (_isProtectedBorrow(refName))
				continue;
			var hasUseAfterCurrent = false;

			for (var j = startIndex; j < stmts.Count; j++)
			{
				if (NodeContainsRefUse(stmts[j], refName))
				{
					hasUseAfterCurrent = true;
					break;
				}
			}

			if (!hasUseAfterCurrent)
				toRelease.Add(refName);
		}

		foreach (var refName in toRelease)
			_removeBorrower(refName);
	}

	/// <summary>
	/// Checks whether an expression references a binding before the non-lexical borrow can end.
	/// The parser stores <c>r.Method(...)</c> in <see cref="CallExpressionSyntax.FunctionName"/>,
	/// not in a receiver AST child; checking only the call arguments can release <c>r</c>
	/// immediately before its final use. For all other expressions, visit the actual AST children
	/// rather than maintaining an incomplete whitelist as new expression forms are added.
	/// </summary>
	internal static bool ExpressionContainsRefUse(ExpressionSyntax expr, string refName)
	{
		if (expr is IdentifierExpressionSyntax id && id.Name == refName)
			return true;

		if (expr is CallExpressionSyntax call &&
			(call.FunctionName == refName || call.FunctionName.StartsWith(refName + ".", StringComparison.Ordinal)))
			return true;

		return expr.GetChildren().Any(child => NodeContainsRefUse(child, refName));
	}

	/// <summary>
	/// Conservatively searches a statement or expression for a live reference use, including
	/// switch arms, optional chains, coalescing, catch bodies and labelled blocks. Declared
	/// bindings/shadowing remain a separate provenance problem; this scan never uses a missing
	/// child as evidence that a borrow can safely end.
	/// </summary>
	private static bool NodeContainsRefUse(SyntaxNode node, string refName)
	{
		if (node is ExpressionSyntax expr)
			return ExpressionContainsRefUse(expr, refName);

		return node.GetChildren().Any(child => NodeContainsRefUse(child, refName));
	}
}
