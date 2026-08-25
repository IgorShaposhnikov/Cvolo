using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Borrowing;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
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
				if (member is FunctionDeclarationSyntax func && func.GenericParameters.Count == 0)
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
			if (type != null) scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true, Origin = OriginKind.Parameter });
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
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, id.Span, $"Use of possibly-uninitialized variable '{id.Name}'");
				}
				break;

			case BinaryExpressionSyntax bin:
				if (bin.Operator == "=")
				{
					AnalyzeExpression(bin.Right, scope);

					// Resolve the base variable being initialized (e.g. 'myPoint' from 'myPoint.p1.X')
					var baseVarName = GetBaseIdentifierName(bin.Left);
					if (baseVarName != null)
					{
						if (scope.Lookup(baseVarName) is VariableSymbol baseSymbol)
							baseSymbol.IsInitialized = true;
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
				// If we have resolved metadata, mark any variable passed to a mutable 'refvar' parameter as initialized!
				if (context.ResolvedCalls.TryGetValue(call, out var func))
				{
					for (var i = 0; i < call.Arguments.Count; i++)
					{
						var arg = call.Arguments[i];

						// Guard to prevent index out of bounds on variadic argument overflow
						if (i < func.Parameters.Count)
						{
							var param = func.Parameters[i];

							if (param.Type is PointerTypeSymbol ptr && ptr.IsMutable)
							{
								var baseName = GetBaseIdentifierName(arg);
								if (baseName != null && scope.Lookup(baseName) is VariableSymbol sym)
								{
									sym.IsInitialized = true;
								}
							}
						}

						AnalyzeExpression(arg, scope);
					}
				}
				else
				{
					foreach (var arg in call.Arguments) AnalyzeExpression(arg, scope);
				}
				break;
		}
	}

	private string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id) return id.Name;
		if (expr is MemberAccessExpressionSyntax m) return GetBaseIdentifierName(m.Expression);
		if (expr is BorrowExpressionSyntax b) return GetBaseIdentifierName(b.Expression);
		return null;
	}
}
