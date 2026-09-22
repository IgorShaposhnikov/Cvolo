using Cvolo.Analysis.Symbols.Borrowing;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns per-function borrow exclusivity state, parent-variable locks, and non-lexical
/// early-release bookkeeping. Value-move analysis is owned by <see cref="MoveAnalyzer"/>, while
/// safe-delegate provenance is owned by <see cref="SafeDelegateAnalyzer"/>.
/// </summary>
internal sealed class BorrowTracker(
	BindingContext context,
	Func<ExpressionSyntax, string?> getBaseIdentifierName,
	Func<SafetyTier> getCurrentTier)
{
	private readonly List<BorrowSymbol> _activeBorrows = [];
	private readonly Dictionary<string, (string BorrowedName, bool IsMutable, int LastUseEnd, TextSpan DeclSpan)> _activeRefs = [];
	private readonly Dictionary<string, HashSet<string>> _parentLocks = [];
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;
	private readonly Func<SafetyTier> _getCurrentTier = getCurrentTier;

	/// <summary>Snapshot used to release borrows and reference names created inside one lexical block.</summary>
	internal readonly record struct BlockState(int BorrowCount, HashSet<string> RefNames);

	/// <summary>Clears all per-function borrow state.</summary>
	public void Reset()
	{
		_activeBorrows.Clear();
		_activeRefs.Clear();
		_parentLocks.Clear();
	}

	/// <summary>Returns whether a value currently has one or more child-reference locks.</summary>
	public bool HasParentLock(string name) => _parentLocks.ContainsKey(name);

	/// <summary>Captures the borrow state visible on entry to a lexical block.</summary>
	public BlockState CaptureBlockState() => new(_activeBorrows.Count, new HashSet<string>(_activeRefs.Keys));

	/// <summary>
	/// Releases borrows and reference names introduced after a block snapshot and returns the names
	/// whose separate lifetime-provenance state should also be discarded by the caller.
	/// </summary>
	public IReadOnlyList<string> ExitBlock(BlockState state)
	{
		if (_activeBorrows.Count > state.BorrowCount)
			_activeBorrows.RemoveRange(state.BorrowCount, _activeBorrows.Count - state.BorrowCount);

		var refsToRemove = _activeRefs.Keys.Where(k => !state.RefNames.Contains(k)).ToList();
		foreach (var name in refsToRemove)
		{
			_activeRefs.Remove(name);
			ReleaseParentLock(name);
		}

		return refsToRemove;
	}

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
			RemoveBorrower(refName);
	}

	/// <summary>
	/// Applies declaration-time borrow exclusivity rules for explicit <c>ref</c>/<c>refvar</c>
	/// bindings and registers the borrow when a base identifier can be determined.
	/// </summary>
	public void VerifyDeclarationBorrow(VariableDeclarationSyntax varDecl)
	{
		// Borrow exclusivity checks are disabled in unbound and unsafe tiers.
		if (_getCurrentTier() != SafetyTier.Safe)
			return;

		if ((varDecl.Type == "refvar" || varDecl.Type == "ref") && varDecl.Initializer is BorrowExpressionSyntax borrow)
		{
			var borrowedName = _getBaseIdentifierName(borrow.Expression);
			if (borrowedName != null)
			{
				var isMutable = varDecl.Type == "refvar";

				// Array Index Locking: borrowing any element blocks all other element borrows.
				var isIndexBorrow = borrow.Expression is IndexExpressionSyntax;
				if (isIndexBorrow && _parentLocks.ContainsKey(borrowedName))
				{
					context.Diagnostics.Report(context.CurrentUnit!.Context, varDecl.Span,
						$"'{borrowedName}' is already borrowed; cannot borrow multiple elements of the same array");
				}

				// Exclusive Mutability: check parent-level conflicts.
				var conflicts = _activeBorrows.Where(b => b.BorrowedName == borrowedName).ToList();
				if (conflicts.Count > 0 && (isMutable || conflicts.Any(c => c.IsMutable)))
				{
					context.Diagnostics.Report(context.CurrentUnit!.Context, varDecl.Span,
						$"Cannot borrow '{borrowedName}' because an incompatible borrow is already active");
				}

				RegisterBorrow(varDecl.Name, borrowedName, isMutable, varDecl.Span);
			}
		}
	}

	/// <summary>
	/// Registers a borrow directly. This is used for compiler-created promoted references and the
	/// immutable lock associated with a ref-capturing lambda.
	/// </summary>
	public void RegisterBorrow(string borrowerName, string borrowedName, bool isMutable, TextSpan span)
	{
		_activeBorrows.Add(new BorrowSymbol(borrowerName, borrowedName, isMutable, span));
		_activeRefs[borrowerName] = (borrowedName, isMutable, span.End, span);
		RegisterParentLock(borrowedName, borrowerName);
	}

	/// <summary>Removes one borrower from the active borrow/ref sets and releases its parent lock.</summary>
	public void RemoveBorrower(string borrowerName)
	{
		_activeRefs.Remove(borrowerName);
		_activeBorrows.RemoveAll(b => b.BorrowerName == borrowerName);
		ReleaseParentLock(borrowerName);
	}

	/// <summary>Reports a move or reassignment while the value still has an active child borrow.</summary>
	public void VerifyUnlocked(ExpressionSyntax expression, string verb)
	{
		var name = _getBaseIdentifierName(expression);
		if (name != null && _parentLocks.ContainsKey(name))
		{
			context.Diagnostics.Report(context.CurrentUnit!.Context, expression.Span,
				$"Cannot {verb} '{name}' while a field borrow is still active");
		}
	}

	/// <summary>Checks recursively whether an expression references the supplied identifier.</summary>
	internal static bool ExpressionContainsRefUse(ExpressionSyntax expr, string refName)
	{
		return expr switch
		{
			IdentifierExpressionSyntax id => id.Name == refName,
			MemberAccessExpressionSyntax m => ExpressionContainsRefUse(m.Expression, refName),
			IndexExpressionSyntax idx => ExpressionContainsRefUse(idx.Left, refName) || ExpressionContainsRefUse(idx.Index, refName),
			BorrowExpressionSyntax borrow => ExpressionContainsRefUse(borrow.Expression, refName),
			CallExpressionSyntax call => call.Arguments.Any(a => ExpressionContainsRefUse(a, refName)),
			BinaryExpressionSyntax bin => ExpressionContainsRefUse(bin.Left, refName) || ExpressionContainsRefUse(bin.Right, refName),
			StructInitializationExpressionSyntax init => init.Initializers.Any(f => ExpressionContainsRefUse(f.Expression, refName)),
			_ => false
		};
	}

	/// <summary>Checks recursively whether a syntax node references the supplied identifier.</summary>
	private static bool NodeContainsRefUse(SyntaxNode node, string refName)
	{
		return node switch
		{
			BlockStatementSyntax block => block.Statements.Any(s => NodeContainsRefUse(s, refName)),
			IfStatementSyntax ifStmt =>
				NodeContainsRefUse(ifStmt.Condition, refName) ||
				NodeContainsRefUse(ifStmt.ThenStatement, refName) ||
				(ifStmt.ElseClause != null && NodeContainsRefUse(ifStmt.ElseClause.Body, refName)),
			WhileStatementSyntax whileStmt =>
				NodeContainsRefUse(whileStmt.Condition, refName) ||
				NodeContainsRefUse(whileStmt.Body, refName),
			ForStatementSyntax forStmt =>
				(forStmt.Initializer != null && NodeContainsRefUse(forStmt.Initializer, refName)) ||
				NodeContainsRefUse(forStmt.Condition, refName) ||
				NodeContainsRefUse(forStmt.Increment, refName) ||
				NodeContainsRefUse(forStmt.Body, refName),
			ForEachStatementSyntax forEach =>
				NodeContainsRefUse(forEach.Collection, refName) ||
				NodeContainsRefUse(forEach.Body, refName),
			ReturnStatementSyntax ret => ret.Expression != null && ExpressionContainsRefUse(ret.Expression, refName),
			ExpressionStatementSyntax exprStmt => ExpressionContainsRefUse(exprStmt.Expression, refName),
			VariableDeclarationSyntax varDecl => varDecl.Initializer != null && ExpressionContainsRefUse(varDecl.Initializer, refName),
			ExpressionSyntax expr => ExpressionContainsRefUse(expr, refName),
			_ => false
		};
	}

	/// <summary>Adds a child-reference lock for the parent value.</summary>
	private void RegisterParentLock(string parentName, string refName)
	{
		if (!_parentLocks.TryGetValue(parentName, out var refs))
		{
			refs = [];
			_parentLocks[parentName] = refs;
		}
		refs.Add(refName);
	}

	/// <summary>Removes a child-reference lock from every parent that currently owns it.</summary>
	private void ReleaseParentLock(string refName)
	{
		var parentKeys = _parentLocks.Where(kv => kv.Value.Contains(refName)).Select(kv => kv.Key).ToList();
		foreach (var parent in parentKeys)
		{
			_parentLocks[parent].Remove(refName);
			if (_parentLocks[parent].Count == 0)
				_parentLocks.Remove(parent);
		}
	}
}
