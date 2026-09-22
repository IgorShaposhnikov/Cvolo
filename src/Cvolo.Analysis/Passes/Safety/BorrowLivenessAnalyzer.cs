using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
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
	Action<string> removeBorrower)
{
	private readonly IReadOnlyDictionary<string, (string BorrowedName, bool IsMutable, int LastUseEnd, TextSpan DeclSpan)> _activeRefs = activeRefs;
	private readonly Action<string> _removeBorrower = removeBorrower;

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
			_removeBorrower(refName);
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
}
