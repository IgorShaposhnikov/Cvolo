using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Coordinates the single recursive syntax traversal for safety analysis and dispatches each
/// structural check to the analyzer that owns its state and diagnostics.
/// </summary>
internal sealed class SafetyTraversal(
	BindingContext context,
	BorrowTracker borrows,
	UnsafeContextValidator unsafeContext,
	ReferenceLifetimeAnalyzer referenceLifetimes,
	MoveAnalyzer moves,
	UnboundValidator unbound,
	ForEachSafetyValidator forEachSafety,
	SafeDelegateAnalyzer safeDelegates,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	/// <summary>
	/// Walks one block in source order, preserving non-lexical borrow release and block-exit
	/// lifetime cleanup around the existing statement traversal.
	/// </summary>
	public void CheckBlockSafety(BlockStatementSyntax block, SymbolTable scope, FunctionDeclarationSyntax func)
	{
		if (block is null)
			return;

		var borrowState = borrows.CaptureBlockState();
		var stmts = block.Statements;
		for (var i = 0; i < stmts.Count; i++)
		{
			var stmt = stmts[i];
			borrows.ReleaseExpiredBorrows(stmt.Span.Start, block, i);
			CheckStatementSafety(stmt, scope, func);
		}

		// Release all borrows and refs taken in this block at block exit.
		foreach (var name in borrows.ExitBlock(borrowState))
			referenceLifetimes.RemoveVariable(name);
	}

	/// <summary>
	/// Dispatches one statement to the existing safety checks without introducing an additional
	/// syntax walk or changing their source-order execution.
	/// </summary>
	private void CheckStatementSafety(SyntaxNode stmt, SymbolTable scope, FunctionDeclarationSyntax func)
	{
		if (stmt is null)
			return;

		switch (stmt)
		{
			case VariableDeclarationSyntax v:
				if (context.VariableSymbols.TryGetValue(v, out var sym))
				{
					scope.Declare(sym);
					if (v.Initializer != null)
					{
						CheckExpressionSafety(v.Initializer, scope);
						moves.EmitLargeCopyWarningIfNeeded(v.Initializer, scope);

						referenceLifetimes.TrackDeclaration(v, sym, scope);

						unbound.TrackLocalReferenceDeclaration(v);

						safeDelegates.TrackDeclaration(v, sym, scope);
					}

					// CVL1005: Raw pointer variables cannot be declared outside unsafe
					unsafeContext.ValidateRawPointerDeclaration(sym, v.Span);

					borrows.VerifyDeclarationBorrow(v);
				}

				break;

			case SwitchStatementSyntax sw:
				CheckSwitchStatementSafety(sw, scope, func);
				break;

			case ReturnStatementSyntax r:
				if (r.Expression != null)
					CheckExpressionSafety(r.Expression, scope);
				if (r.Expression != null)
					safeDelegates.ValidateReturn(r.Expression, scope);
				referenceLifetimes.VerifyReturnLifetime(r, func, scope);
				break;

			case ExpressionStatementSyntax e:
				CheckExpressionSafety(e.Expression, scope);
				break;

			case IfStatementSyntax i:
				CheckExpressionSafety(i.Condition, scope);
				CheckStatementSafety(i.ThenStatement, scope, func);
				if (i.ElseClause != null)
					CheckStatementSafety(i.ElseClause.Body, scope, func);
				break;

			case BlockStatementSyntax b:
				CheckBlockSafety(b, new SymbolTable(scope), func);
				break;

			case LabeledBlockStatementSyntax lb:
				CheckBlockSafety(lb.Body, new SymbolTable(scope), func);
				break;

			case WhileStatementSyntax w:
				CheckExpressionSafety(w.Condition, scope);
				if (w.Body is BlockStatementSyntax wBlock)
					CheckBlockSafety(wBlock, new SymbolTable(scope), func);
				else
					CheckStatementSafety(w.Body, scope, func);
				break;

			case ForStatementSyntax f:
				if (f.Initializer != null)
					CheckStatementSafety(f.Initializer, scope, func);
				CheckExpressionSafety(f.Condition, scope);
				CheckExpressionSafety(f.Increment, scope);
				if (f.Body is BlockStatementSyntax fBlock)
					CheckBlockSafety(fBlock, new SymbolTable(scope), func);
				else
					CheckStatementSafety(f.Body, scope, func);
				break;

			case ForEachStatementSyntax fe:
				CheckExpressionSafety(fe.Collection, scope);
				if (fe.Body is BlockStatementSyntax feBlock)
				{
					forEachSafety.Validate(fe, feBlock);
					CheckBlockSafety(feBlock, new SymbolTable(scope), func);
				}
				else
					CheckStatementSafety(fe.Body, scope, func);
				break;

			case UnsafeBlockStatementSyntax unsafeBlock:
				unsafeContext.Push(SafetyTier.Unsafe);
				CheckBlockSafety(unsafeBlock.Body, new SymbolTable(scope), func);
				unsafeContext.Pop();
				break;

			case BreakStatementSyntax:
			case ContinueStatementSyntax:
				break;
		}
	}

	/// <summary>
	/// Walks one expression using the original recursive order and delegates stateful checks to the
	/// existing move, unbound, lifetime, borrow, unsafe-context, and safe-delegate analyzers.
	/// </summary>
	public void CheckExpressionSafety(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				moves.VerifyReadable(id, scope);
				break;

			case NullLiteralExpressionSyntax:
				unsafeContext.ValidateNullLiteral(expr.Span);
				break;

			case MemberAccessExpressionSyntax m:
				CheckExpressionSafety(m.Expression, scope);
				unbound.ValidateMemberAccess(m, scope);
				break;

			case IndexExpressionSyntax idx:
				CheckExpressionSafety(idx.Left, scope);
				CheckExpressionSafety(idx.Index, scope);
				break;

			case BorrowExpressionSyntax b:
				CheckExpressionSafety(b.Expression, scope);
				break;

			case UnaryExpressionSyntax u:
				CheckExpressionSafety(u.Operand, scope);
				unsafeContext.ValidateUnaryOperation(u);
				break;

			case StructInitializationExpressionSyntax init:
				foreach (var member in init.Initializers)
					CheckExpressionSafety(member.Expression, scope);
				break;

			case AsmExpressionSyntax asm:
				foreach (var operand in asm.Operands)
					CheckExpressionSafety(operand.Expression, scope);
				break;

			case CallExpressionSyntax call:
				foreach (var arg in call.Arguments)
				{
					CheckExpressionSafety(arg, scope);
					moves.HandleByValueArgument(arg, scope);
				}

				break;

			case LambdaExpressionSyntax lam:
				safeDelegates.ValidateLambda(lam, scope);
				break;

			case BinaryExpressionSyntax bin:
				CheckExpressionSafety(bin.Right, scope);
				if (bin.Operator == "=")
				{
					safeDelegates.ValidateAssignment(bin, scope);
				}

				if (bin.Operator == "=" && bin.Left is IdentifierExpressionSyntax leftId)
				{
					var leftSymbol = scope.Lookup(leftId.Name) as VariableSymbol
						?? context.ResolveGlobalReference(leftId.Name, out _);
					if (leftSymbol is not null)
					{
						borrows.VerifyUnlocked(bin.Left, "reassign");
						moves.ResetMoved(leftSymbol);
						moves.HandleCopyAssignment(bin.Right, scope);

						unbound.ValidateGlobalAssignmentEscape(bin, leftSymbol, scope);
						referenceLifetimes.TrackAssignment(leftId, leftSymbol, bin.Right, bin.Span, scope);

					}
				}
				else
				{
					CheckExpressionSafety(bin.Left, scope);

					unbound.ValidateReferenceFieldAssignment(bin, scope);
				}

				break;
		}
	}

	/// <summary>
	/// Checks each switch arm in the original sequence and brackets reference-pattern promotions
	/// with the same synthetic borrow registration and cleanup used by the inline traversal.
	/// </summary>
	private void CheckSwitchStatementSafety(SwitchStatementSyntax sw, SymbolTable scope, FunctionDeclarationSyntax func)
	{
		CheckExpressionSafety(sw.Expression, scope);
		var parentName = getBaseIdentifierName(sw.Expression);

		foreach (var c in sw.Cases)
		{
			var targetType = resolveExpressionType(sw.Expression, scope);
			var hasRefPromotion = c.VariableName is not null && targetType is PointerTypeSymbol;

			if (hasRefPromotion && parentName is not null)
			{
				var isMutable = targetType is PointerTypeSymbol targetPtr && targetPtr.IsMutable;
				borrows.RegisterBorrow(c.VariableName!, parentName, isMutable, c.Span);
			}

			CheckBlockSafety(new BlockStatementSyntax(c.Span, c.Body), new SymbolTable(scope), func);

			if (hasRefPromotion && parentName is not null)
			{
				borrows.RemoveBorrower(c.VariableName!);
			}
		}
	}
}
