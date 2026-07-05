using Cvolo.Analysis.Symbols;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Analysis.Passes;

public sealed class FlowAnalysisPass(BindingContext context)
{
	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
				if (member is FunctionDeclarationSyntax func)
					AnalyzeFunctionFlow(func);
		}
	}

	private void AnalyzeFunctionFlow(FunctionDeclarationSyntax func)
	{
		var scope = new SymbolTable(context.Globals);

		// 1. Parameters are always initialized on entry
		foreach (var param in func.Parameters)
		{
			var type = context.ResolveType(param.Type);
			if (type != null) scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true });
		}

		AnalyzeBlock(func.Body, scope);
	}

	private void AnalyzeBlock(BlockStatementSyntax block, SymbolTable scope)
	{
		foreach (var stmt in block.Statements)
			AnalyzeStatement(stmt, scope);
	}

	private void AnalyzeStatement(SyntaxNode stmt, SymbolTable scope)
	{
		switch (stmt)
		{
			case VariableDeclarationSyntax v:
				if (context.VariableSymbols.TryGetValue(v, out var sym))
				{
					scope.Declare(sym);
					// Variables with an initializer are marked as initialized immediately
					if (v.Initializer != null)
					{
						AnalyzeExpression(v.Initializer, scope);
						sym.IsInitialized = true;
					}
					else
					{
						sym.IsInitialized = false;
					}
				}
				break;

			case IfStatementSyntax i:
				AnalyzeExpression(i.Condition, scope);

				// Capture initialization states before branches
				AnalyzeStatement(i.ThenStatement, scope);
				if (i.ElseClause != null)
					AnalyzeStatement(i.ElseClause.Body, scope);

				// Note: For a true production compiler, you would perform an "Intersection" 
				// of the then/else states here. For this MVP, we analyze sequentially.
				break;

			case ExpressionStatementSyntax e:
				AnalyzeExpression(e.Expression, scope);
				break;

			case ReturnStatementSyntax r:
				if (r.Expression != null) AnalyzeExpression(r.Expression, scope);
				break;

			case BlockStatementSyntax b:
				AnalyzeBlock(b, new SymbolTable(scope));
				break;
		}
	}

	private void AnalyzeExpression(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				var symbol = scope.Lookup(id.Name) as VariableSymbol;
				if (symbol != null && !symbol.IsInitialized)
				{
					context.Diagnostics.Report(id.Span, $"Use of possibly-uninitialized variable '{id.Name}'");
				}
				break;

			case BinaryExpressionSyntax bin:
				if (bin.Operator == "=")
				{
					AnalyzeExpression(bin.Right, scope);
					// RULE: Assignment initializes the left-hand variable
					if (bin.Left is IdentifierExpressionSyntax leftId)
					{
						var leftSymbol = scope.Lookup(leftId.Name) as VariableSymbol;
						if (leftSymbol != null) leftSymbol.IsInitialized = true;
					}
				}
				else
				{
					AnalyzeExpression(bin.Left, scope);
					AnalyzeExpression(bin.Right, scope);
				}
				break;

			case MemberAccessExpressionSyntax m:
				AnalyzeExpression(m.Expression, scope);
				break;

			case CallExpressionSyntax call:
				foreach (var arg in call.Arguments) AnalyzeExpression(arg, scope);
				break;
		}
	}
}
