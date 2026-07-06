using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Borrowing;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Analysis.Passes;

public sealed class SafetyPass(BindingContext context)
{
	private readonly List<BorrowSymbol> _activeBorrows = [];

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
		// Parameters are always valid on entry
		foreach (var param in func.Parameters)
		{
			var type = context.ResolveType(param.Type);
			if (type != null) scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true });
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
				// CRITICAL: Retrieve the ALREADY EXISTING symbol from the context map
				if (context.VariableSymbols.TryGetValue(v, out var sym))
				{
					scope.Declare(sym);
					if (v.Initializer != null) CheckExpressionSafety(v.Initializer, scope);
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

			case CallExpressionSyntax call:
				foreach (var arg in call.Arguments)
				{
					CheckExpressionSafety(arg, scope);
					if (arg is IdentifierExpressionSyntax argId)
					{
						var argSymbol = scope.Lookup(argId.Name) as VariableSymbol;
						// If we pass a struct by value, it is a MOVE
						if (argSymbol != null && argSymbol.Type is StructTypeSymbol)
							argSymbol.IsMoved = true;
					}
				}

				break;

			case BinaryExpressionSyntax bin:
				CheckExpressionSafety(bin.Right, scope);
				if (bin.Operator == "=" && bin.Left is IdentifierExpressionSyntax leftId)
				{
					if (scope.Lookup(leftId.Name) is VariableSymbol leftSymbol) leftSymbol.IsMoved = false; // Re-initialize
				}
				else
				{
					CheckExpressionSafety(bin.Left, scope);
				}

				break;
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
		if (ret.Expression is BorrowExpressionSyntax borrow && borrow.Expression is IdentifierExpressionSyntax id)
		{
			if (!func.Parameters.Any(p => p.Name == id.Name))
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
