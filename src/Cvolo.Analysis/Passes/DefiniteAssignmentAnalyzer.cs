using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Analysis.Passes;

/// <summary>
/// Performs the flow pass's per-function definite-assignment analysis.
/// The analysis intentionally preserves the current sequential branch model rather than
/// attempting control-flow state intersections.
/// </summary>
internal sealed class DefiniteAssignmentAnalyzer(BindingContext context)
{
	/// <summary>
	/// Analyzes a concrete function body, seeding its local scope with initialized parameters.
	/// </summary>л
	/// </summary>л
	public void Analyze(FunctionDeclarationSyntax func)
	{
		if (!func.HasBody)
			return;

		var scope = new SymbolTable(context.Globals);

		// Parameters are initialized on function entry.
		foreach (var param in func.Parameters)
		{
			var type = context.ResolveType(param.Type);
			if (type != null)
				scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true, Origin = OriginKind.Parameter });
		}

		AnalyzeBlock(func.Body, scope);
	}

	/// <summary>
	/// Analyzes the statements of a block using the supplied lexical scope.
	/// </summary>
	private void AnalyzeBlock(BlockStatementSyntax? block, SymbolTable scope)
	{
		if (block is null)
			return;

		foreach (var stmt in block.Statements)
			AnalyzeStatement(stmt, scope);
	}

	/// <summary>
	/// Applies definite-assignment state transitions for a single statement.
	/// </summary>
	private void AnalyzeStatement(SyntaxNode stmt, SymbolTable scope)
	{
		if (stmt is null)
			return;

		switch (stmt)
		{
			case VariableDeclarationSyntax v:
				if (context.VariableSymbols.TryGetValue(v, out var sym))
				{
					scope.Declare(sym);
					// Variables with an initializer are marked as initialized immediately.
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

				// Preserve the existing MVP behavior: branches are analyzed sequentially
				// instead of intersecting their outgoing initialization states.
				AnalyzeStatement(i.ThenStatement, scope);
				if (i.ElseClause != null)
					AnalyzeStatement(i.ElseClause.Body, scope);
				break;

			case ExpressionStatementSyntax e:
				AnalyzeExpression(e.Expression, scope);
				break;

			case ReturnStatementSyntax r:
				if (r.Expression != null)
					AnalyzeExpression(r.Expression, scope);
				break;

			case BlockStatementSyntax b:
				AnalyzeBlock(b, new SymbolTable(scope));
				break;

			case LabeledBlockStatementSyntax lb:
				AnalyzeBlock(lb.Body, new SymbolTable(scope));
				break;
		}
	}

	/// <summary>
	/// Reads and updates definite-assignment state for the expression forms handled by the flow pass.
	/// </summary>
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

					// Resolve the base variable being initialized (e.g. 'myPoint' from 'myPoint.p1.X').
					var baseVarName = GetBaseIdentifierName(bin.Left);
					if (baseVarName != null && scope.Lookup(baseVarName) is VariableSymbol baseSymbol)
						baseSymbol.IsInitialized = true;
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
				// Mutable reference parameters may initialize the variable passed to them.
				if (context.ResolvedCalls.TryGetValue(call, out var func))
				{
					for (var i = 0; i < call.Arguments.Count; i++)
					{
						var arg = call.Arguments[i];

						// Guard variadic overflow beyond the declared parameter list.
						if (i < func.Parameters.Count)
						{
							var param = func.Parameters[i];
							if (param.Type is PointerTypeSymbol ptr && ptr.IsMutable)
							{
								var baseName = GetBaseIdentifierName(arg);
								if (baseName != null && scope.Lookup(baseName) is VariableSymbol sym)
									sym.IsInitialized = true;
							}
						}

						AnalyzeExpression(arg, scope);
					}
				}
				else
				{
					foreach (var arg in call.Arguments)
						AnalyzeExpression(arg, scope);
				}
				break;
		}
	}

	/// <summary>
	/// Returns the root variable name of an assignable expression, following member access and borrows.
	/// </summary>
	private static string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
			return id.Name;
		if (expr is MemberAccessExpressionSyntax m)
			return GetBaseIdentifierName(m.Expression);
		if (expr is BorrowExpressionSyntax b)
			return GetBaseIdentifierName(b.Expression);
		return null;
	}
}
