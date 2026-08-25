using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Borrowing;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes;

public sealed class SafetyPass(BindingContext context)
{
	private readonly List<BorrowSymbol> _activeBorrows = [];
	private ClassificationAnalyzer? _classification;

	private ClassificationAnalyzer Classification => _classification ??= new ClassificationAnalyzer(context);

	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
				if (member is FunctionDeclarationSyntax func && func.GenericParameters.Count == 0)
					CheckFunctionSafety(func);
		}
	}

	private void CheckFunctionSafety(FunctionDeclarationSyntax func)
	{
		_activeBorrows.Clear();
		var scope = new SymbolTable(context.Globals);
		foreach (var param in func.Parameters)
		{
			var type = context.ResolveType(param.Type);
			if (type != null) scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true, Origin = OriginKind.Parameter });
		}
		CheckBlockSafety(func.Body, scope, func);
	}

	private void CheckBlockSafety(BlockStatementSyntax block, SymbolTable scope, FunctionDeclarationSyntax func)
	{
		var borrowCountBefore = _activeBorrows.Count;
		foreach (var stmt in block.Statements)
			CheckStatementSafety(stmt, scope, func);

		if (_activeBorrows.Count > borrowCountBefore)
			_activeBorrows.RemoveRange(borrowCountBefore, _activeBorrows.Count - borrowCountBefore);
	}

	private void CheckStatementSafety(SyntaxNode stmt, SymbolTable scope, FunctionDeclarationSyntax func)
	{
		switch (stmt)
		{
		case VariableDeclarationSyntax v:
			if (context.VariableSymbols.TryGetValue(v, out var sym))
			{
				scope.Declare(sym);
				if (v.Initializer != null)
				{
					CheckExpressionSafety(v.Initializer, scope);
					EmitLargeCopyWarningIfNeeded(v.Initializer, scope);

					// Propagate origin through ref/refvar declarations
					if (v.Type is "ref" or "refvar" && v.Initializer is BorrowExpressionSyntax borrowExpr)
					{
						var borrowedName = GetBaseIdentifierName(borrowExpr.Expression);
						if (borrowedName != null && scope.Lookup(borrowedName) is VariableSymbol borrowed)
							sym.Origin = borrowed.Origin;
					}
				}
				VerifyBorrowRules(v, scope);
			}

			break;

			case ReturnStatementSyntax r:
				if (r.Expression != null) CheckExpressionSafety(r.Expression, scope);
				VerifyReturnLifetime(r, func, scope);
				break;

			case ExpressionStatementSyntax e:
				CheckExpressionSafety(e.Expression, scope);
				break;

			case IfStatementSyntax i:
				CheckExpressionSafety(i.Condition, scope);
				CheckStatementSafety(i.ThenStatement, scope, func);
				if (i.ElseClause != null) CheckStatementSafety(i.ElseClause.Body, scope, func);
				break;

			case BlockStatementSyntax b:
				CheckBlockSafety(b, new SymbolTable(scope), func);
				break;
		}
	}

	private void CheckExpressionSafety(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				if (scope.Lookup(id.Name) is VariableSymbol symbol && symbol.IsMoved)
					context.Diagnostics.Report(context.CurrentUnit!.Context, id.Span, $"Use of moved variable '{id.Name}'");
				break;

			case MemberAccessExpressionSyntax m:
				CheckExpressionSafety(m.Expression, scope);
				break;

			case IndexExpressionSyntax idx:
				CheckExpressionSafety(idx.Left, scope);
				CheckExpressionSafety(idx.Index, scope);
				break;

			case BorrowExpressionSyntax b:
				CheckExpressionSafety(b.Expression, scope);
				break;

			case CallExpressionSyntax call:
				foreach (var arg in call.Arguments)
				{
					CheckExpressionSafety(arg, scope);
					HandleByValueArgument(arg, scope);
				}

				break;

		case BinaryExpressionSyntax bin:
			CheckExpressionSafety(bin.Right, scope);
			if (bin.Operator == "=" && bin.Left is IdentifierExpressionSyntax leftId)
			{
				if (scope.Lookup(leftId.Name) is VariableSymbol leftSymbol)
				{
					leftSymbol.IsMoved = false;
					HandleCopyAssignment(bin.Right, scope);

					// Propagate origin on ref/refvar reassignment
					if (leftSymbol.Type is PointerTypeSymbol)
					{
						if (bin.Right is BorrowExpressionSyntax rb)
						{
							var rightName = GetBaseIdentifierName(rb.Expression);
							if (rightName != null && scope.Lookup(rightName) is VariableSymbol rightSym)
								leftSymbol.Origin = rightSym.Origin;
						}
						else if (bin.Right is IdentifierExpressionSyntax rightId && scope.Lookup(rightId.Name) is VariableSymbol rightSym2 && rightSym2.Type is PointerTypeSymbol)
						{
							leftSymbol.Origin = rightSym2.Origin;
						}
					}
				}
			}
			else
			{
				CheckExpressionSafety(bin.Left, scope);
			}

			break;
		}
	}

	private void HandleByValueArgument(ExpressionSyntax arg, SymbolTable scope)
	{
		if (arg is StructInitializationExpressionSyntax or BorrowExpressionSyntax)
			return;

		var type = ResolveExpressionType(arg, scope);

		if (type is StructTypeSymbol argStruct)
		{
			var kind = Classification.Classify(argStruct);
			switch (kind)
			{
				case CopyKind.ResourceMove:
					if (arg is IdentifierExpressionSyntax aid && scope.Lookup(aid.Name) is VariableSymbol av)
						av.IsMoved = true;
					break;
				case CopyKind.LargeCopy:
					var size = Classification.CalculateByteSize(argStruct);
					context.Diagnostics.ReportWarning(
						context.CurrentUnit!.Context, arg.Span,
						$"'{argStruct.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
						DiagnosticIds.LargeCopyWarning);
					break;
			}
		}
		else if (type is SliceTypeSymbol)
		{
			if (arg is IdentifierExpressionSyntax sid && scope.Lookup(sid.Name) is VariableSymbol sv)
				sv.IsMoved = true;
		}
	}

	private void EmitLargeCopyWarningIfNeeded(ExpressionSyntax expr, SymbolTable scope)
	{
		if (expr is StructInitializationExpressionSyntax)
			return;

		var type = ResolveExpressionType(expr, scope);
		if (type is StructTypeSymbol st)
		{
			var kind = Classification.Classify(st);
			if (kind == CopyKind.LargeCopy)
			{
				var size = Classification.CalculateByteSize(st);
				context.Diagnostics.ReportWarning(
					context.CurrentUnit!.Context, expr.Span,
					$"'{st.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
					DiagnosticIds.LargeCopyWarning);
			}
		}
	}

	private TypeSymbol? ResolveExpressionType(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			IdentifierExpressionSyntax id => scope.Lookup(id.Name) is VariableSymbol v ? v.Type : null,
			CallExpressionSyntax call => context.ResolvedCalls.TryGetValue(call, out var func) ? func.ReturnType : null,
			StructInitializationExpressionSyntax init => context.ResolveType(init.StructTypeName),
			BorrowExpressionSyntax borrow => ResolveExpressionType(borrow.Expression, scope),
			_ => null
		};
	}

	private void HandleCopyAssignment(ExpressionSyntax rightExpr, SymbolTable scope)
	{
		if (rightExpr is not IdentifierExpressionSyntax rightId)
			return;

		var rightSymbol = scope.Lookup(rightId.Name) as VariableSymbol;
		if (rightSymbol == null || rightSymbol.Type is not StructTypeSymbol rightStruct)
			return;

		var kind = Classification.Classify(rightStruct);
		if (kind == CopyKind.LargeCopy)
		{
			var size = Classification.CalculateByteSize(rightStruct);
			context.Diagnostics.ReportWarning(
				context.CurrentUnit!.Context, rightId.Span,
				$"'{rightId.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
				DiagnosticIds.LargeCopyWarning);
		}
	}

	private void VerifyBorrowRules(VariableDeclarationSyntax varDecl, SymbolTable scope)
	{
		if ((varDecl.Type == "refvar" || varDecl.Type == "ref") && varDecl.Initializer is BorrowExpressionSyntax borrow)
		{
			var borrowedName = GetBaseIdentifierName(borrow.Expression);
			if (borrowedName != null)
			{
				var isMutable = varDecl.Type == "refvar";
				var conflicts = _activeBorrows.Where(b => b.BorrowedName == borrowedName).ToList();
				if (conflicts.Count > 0)
				{
					if (isMutable || conflicts.Any(c => c.IsMutable))
					{
						context.Diagnostics.Report(context.CurrentUnit!.Context, varDecl.Span, $"Cannot borrow '{borrowedName}' because an incompatible borrow is already active");
					}
				}

				_activeBorrows.Add(new BorrowSymbol(varDecl.Name, borrowedName, isMutable, varDecl.Span));
			}
		}
	}

	private void VerifyReturnLifetime(ReturnStatementSyntax ret, FunctionDeclarationSyntax func, SymbolTable scope)
	{
		if (ret.Expression == null) return;

		// Case 1: return ref expr; — BorrowExpressionSyntax wrapping an identifier
		if (ret.Expression is BorrowExpressionSyntax borrow && borrow.Expression is IdentifierExpressionSyntax bid)
		{
			if (scope.Lookup(bid.Name) is VariableSymbol sym && sym.Origin == OriginKind.Local)
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span, $"Cannot return reference to local variable '{bid.Name}' (dangling reference)");
			}
			return;
		}

		// Case 2: return r; where r is a ref/refvar variable (PointerTypeSymbol)
		if (ret.Expression is IdentifierExpressionSyntax id)
		{
			if (scope.Lookup(id.Name) is VariableSymbol idSym && idSym.Type is PointerTypeSymbol && idSym.Origin == OriginKind.Local)
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span, $"Cannot return reference to local variable '{id.Name}' (dangling reference)");
			}
		}
	}

	private string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id) return id.Name;
		if (expr is MemberAccessExpressionSyntax m) return GetBaseIdentifierName(m.Expression);
		return null;
	}
}
