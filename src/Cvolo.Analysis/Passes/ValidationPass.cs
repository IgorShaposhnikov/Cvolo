using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Analysis.Passes;

public sealed class ValidationPass(BindingContext context)
{
	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func)
					CheckFunctionBody(func);
			}
		}
	}

	private void CheckFunctionBody(FunctionDeclarationSyntax func)
	{
		var localScope = new SymbolTable(context.Globals);

		foreach (var param in func.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is not null)
			{
				var varSymbol = new VariableSymbol(param.Name, paramType, isMutable: false)
				{
					IsInitialized = true
				};
				localScope.Declare(varSymbol);
			}
		}

		CheckBlock(func.Body, localScope, func);
	}

	private void CheckBlock(BlockStatementSyntax block, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		foreach (var stmt in block.Statements)
		{
			CheckStatement(stmt, scope, currentFunc);
		}
	}

	private void CheckStatement(SyntaxNode stmt, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		switch (stmt)
		{
			case ReturnStatementSyntax ret:
				CheckReturnStatement(ret, scope, currentFunc);
				break;
			case ExpressionStatementSyntax exprStmt:
				CheckExpression(exprStmt.Expression, scope);
				break;
			case VariableDeclarationSyntax varDecl:
				CheckVariableDeclaration(varDecl, scope, currentFunc);
				break;
			case BlockStatementSyntax block:
				CheckBlock(block, new SymbolTable(scope), currentFunc);
				break;
			case IfStatementSyntax ifStmt:
				CheckExpression(ifStmt.Condition, scope);
				CheckStatement(ifStmt.ThenStatement, scope, currentFunc);
				if (ifStmt.ElseClause is not null)
					CheckStatement(ifStmt.ElseClause.Body, scope, currentFunc);
				break;
			case WhileStatementSyntax whileStmt:
				CheckExpression(whileStmt.Condition, scope);
				CheckStatement(whileStmt.Body, scope, currentFunc);
				break;
			case ForStatementSyntax forStmt:
				{
					var forScope = new SymbolTable(scope);
					CheckVariableDeclaration(forStmt.Initializer, forScope, currentFunc);
					CheckExpression(forStmt.Condition, forScope);
					CheckExpression(forStmt.Increment, forScope);
					CheckStatement(forStmt.Body, forScope, currentFunc);
					break;
				}
		}
	}

	private void CheckVariableDeclaration(VariableDeclarationSyntax varDecl, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		var existing = scope.Lookup(varDecl.Name);
		if (existing is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, varDecl.Span, $"Variable '{varDecl.Name}' is already declared in this scope");
		}

		TypeSymbol? resolvedType = null;
		if (varDecl.Initializer is not null)
		{
			CheckExpression(varDecl.Initializer, scope);
			resolvedType = GetExpressionType(varDecl.Initializer, scope);
		}

		if (varDecl.Type == "refvar" || varDecl.Type == "ref")
		{
			if (resolvedType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, varDecl.Span, "Reference type inference requires an initializer");
				return;
			}

			var isMutable = varDecl.Type == "refvar";
			resolvedType = resolvedType is PointerTypeSymbol ptr
				? new PointerTypeSymbol(ptr.ReferencedType, isMutable)
				: new PointerTypeSymbol(resolvedType, isMutable);
		}
		else if (varDecl.Type is not null)
		{
			resolvedType = context.ResolveType(varDecl.Type);

			var initializerType = varDecl.Initializer != null ? GetExpressionType(varDecl.Initializer, scope) : null;
			if (resolvedType != null && initializerType != null && !resolvedType.Equals(initializerType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, varDecl.Span, $"Cannot initialize variable of type '{resolvedType.Name}' with value of type '{initializerType.Name}'");
			}
		}

		resolvedType ??= TypeSymbol.Int;

		var varSymbol = new VariableSymbol(varDecl.Name, resolvedType, varDecl.IsMutable) { IsInitialized = varDecl.Initializer != null };

		scope.Declare(varSymbol);
		context.VariableSymbols[varDecl] = varSymbol;
	}

	private void CheckExpression(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				{
					var symbol = scope.Lookup(id.Name);
					if (symbol is null)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, id.Span, $"Undefined variable '{id.Name}'");
					}

					break;
				}
			case MemberAccessExpressionSyntax memberAccess:
				CheckMemberAccessExpression(memberAccess, scope);
				break;
			case BorrowExpressionSyntax borrow:
				CheckBorrowExpression(borrow, scope);
				break;
			case StructInitializationExpressionSyntax structInit:
				CheckStructInitializationExpression(structInit, scope);
				break;
			case HeapAllocationExpressionSyntax heap:
				CheckExpression(heap.Expression, scope);
				break;
			case ArrayInitializationExpressionSyntax arrInit:
				foreach (var el in arrInit.Elements) CheckExpression(el, scope);
				break;
			case TernaryExpressionSyntax ternary:
				CheckTernaryExpression(ternary, scope);
				break;
			case CallExpressionSyntax call:
				{
					var symbol = ResolveFunction(call.FunctionName, scope);

					if (symbol is null)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.Span, $"Undefined function '{call.FunctionName}'");
						return;
					}

					if (symbol is not FunctionSymbol func)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.Span, $"'{call.FunctionName}' is not a function");
						return;
					}

					var argCount = call.Arguments.Count;
					var paramCount = func.Parameters.Count;
					var isVariadic = func.IsVariadic;

					if (!isVariadic && argCount != paramCount)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.Span, $"Function '{call.FunctionName}' expects {paramCount} arguments but received {argCount}");
						return;
					}

					if (isVariadic && argCount < paramCount)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.Span, $"Function '{call.FunctionName}' expects at least {paramCount} arguments but received {argCount}");
						return;
					}

					for (var i = 0; i < call.Arguments.Count; i++)
					{
						var arg = call.Arguments[i];
						CheckExpression(arg, scope);

						// If the argument is a variable identifier
						if (arg is IdentifierExpressionSyntax id)
						{
							var argSymbol = scope.Lookup(id.Name) as VariableSymbol;
							if (argSymbol is not null)
							{
								// Safely check parameter type, accounting for variadic arguments (like in printf)
								var isPointerParam = i < func.Parameters.Count && func.Parameters[i].Type is PointerTypeSymbol;
							}
						}
					}

					break;
				}
			case BinaryExpressionSyntax bin:
				{
					if (bin.Operator == "=")
					{
						// 1. Evaluate the right-hand side first (reads and moves happen here)
						CheckExpression(bin.Right, scope);

						// 2. Evaluate the left-hand side second (re-initialization happens here)
						if (bin.Left is IdentifierExpressionSyntax id)
						{
							var varSymbol = scope.Lookup(id.Name) as VariableSymbol;
							if (varSymbol is not null)
							{
								if (!varSymbol.IsMutable)
								{
									var currentFileContext = context.FileContexts[context.CurrentUnit!];
									context.Diagnostics.Report(currentFileContext, id.Span, $"Cannot assign to immutable variable '{id.Name}'");
								}
							}
							else
							{
								var currentFileContext = context.FileContexts[context.CurrentUnit!];
								context.Diagnostics.Report(currentFileContext, id.Span, $"Undefined variable '{id.Name}'");
							}
						}
						else
						{
							CheckExpression(bin.Left, scope);
						}
					}

					break;
				}
			case UnaryExpressionSyntax unary:
				CheckExpression(unary.Operand, scope);
				break;
		}
	}

	private TypeSymbol? CheckMemberAccessExpression(MemberAccessExpressionSyntax expr, SymbolTable scope)
	{
		CheckExpression(expr.Expression, scope);
		var leftType = GetExpressionType(expr.Expression, scope);
		if (leftType is null) return null;

		// Automatically dereference references to access underlying struct fields
		if (leftType is PointerTypeSymbol pointerType)
		{
			leftType = pointerType.ReferencedType;
		}

		// Slices have a built-in read-only 'Length' field of type 'int'
		if (leftType.Name.EndsWith("[]") && expr.MemberName == "Length")
		{
			return TypeSymbol.Int;
		}

		if (leftType is not StructTypeSymbol structType)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Type '{leftType.Name}' is not a struct; cannot access member '{expr.MemberName}'");
			return null;
		}

		var field = structType.FindField(expr.MemberName);
		if (field is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Struct '{structType.Name}' does not contain field '{expr.MemberName}'");
			return null;
		}

		return field.Type;
	}

	private TypeSymbol? CheckStructInitializationExpression(StructInitializationExpressionSyntax expr, SymbolTable scope)
	{
		var type = context.ResolveType(expr.StructTypeName);
		if (type is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Unknown type '{expr.StructTypeName}'");
			return null;
		}

		if (type is not StructTypeSymbol structType)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Type '{expr.StructTypeName}' is not a struct type");
			return null;
		}

		var initializedFields = new HashSet<string>();
		foreach (var init in expr.Initializers)
		{
			if (!initializedFields.Add(init.MemberName))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span, $"Duplicate initializer for field '{init.MemberName}'");
				continue;
			}

			var field = structType.FindField(init.MemberName);
			if (field is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span, $"Struct '{structType.Name}' does not contain field '{init.MemberName}'");
				continue;
			}

			CheckExpression(init.Expression, scope);
			var initType = GetExpressionType(init.Expression, scope);
			if (initType is not null && !initType.Equals(field.Type))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span, $"Cannot initialize field '{init.MemberName}' of type '{field.Type.Name}' with value of type '{initType.Name}'");
			}
		}

		foreach (var field in structType.Fields)
		{
			if (!initializedFields.Contains(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span, $"Missing initializer for field '{field.Name}' of struct '{structType.Name}'");
			}
		}

		return structType;
	}

	private TypeSymbol? CheckArrayInitialization(ArrayInitializationExpressionSyntax expr, SymbolTable scope)
	{
		if (expr.Elements.Count == 0) return null; // Can't infer type of empty array easily yet

		var elementType = GetExpressionType(expr.Elements[0], scope) ?? TypeSymbol.Int;

		for (var i = 1; i < expr.Elements.Count; i++)
		{
			var elType = GetExpressionType(expr.Elements[i], scope);
			if (elType is not null && !elType.Equals(elementType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Elements[i].Span, $"Array elements must have the same type. Expected '{elementType.Name}', found '{elType.Name}'");
			}
		}

		return new ArrayTypeSymbol(elementType, expr.Elements.Count);
	}

	private TypeSymbol? CheckBorrowExpression(BorrowExpressionSyntax expr, SymbolTable scope)
	{
		CheckExpression(expr.Expression, scope);
		var innerType = GetExpressionType(expr.Expression, scope);
		if (innerType is null) return null;

		var isMutable = false;
		if (expr.Expression is IdentifierExpressionSyntax id)
		{
			var symbol = scope.Lookup(id.Name) as VariableSymbol;
			if (symbol is not null) isMutable = symbol.IsMutable;
		}

		return new PointerTypeSymbol(innerType, isMutable);
	}

	private TypeSymbol? CheckTernaryExpression(TernaryExpressionSyntax expr, SymbolTable scope)
	{
		CheckExpression(expr.Condition, scope);
		var condType = GetExpressionType(expr.Condition, scope);
		if (condType is not null && !condType.Equals(TypeSymbol.Bool))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Condition.Span, $"Ternary condition must be 'bool', found '{condType.Name}'");
		}

		CheckExpression(expr.ThenExpression, scope);
		CheckExpression(expr.ElseExpression, scope);

		var thenType = GetExpressionType(expr.ThenExpression, scope);
		var elseType = GetExpressionType(expr.ElseExpression, scope);

		if (thenType is not null && elseType is not null && !thenType.Equals(elseType))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Ternary branches must have the same type. Found '{thenType.Name}' and '{elseType.Name}'");
		}

		return thenType;
	}

	private void CheckReturnStatement(ReturnStatementSyntax ret, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		if (ret.Expression is null) return;

		CheckExpression(ret.Expression, scope);

		// ONLY check if the Type matches the signature
		var actualType = GetExpressionType(ret.Expression, scope);
		var expectedType = context.ResolveType(currentFunc.ReturnType);

		if (actualType != null && expectedType != null && !actualType.Equals(expectedType))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ret.Expression.Span, $"Function '{currentFunc.Name}' expects return type '{expectedType.Name}' but found '{actualType.Name}'");
		}
	}

	private TypeSymbol? GetExpressionType(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			IdentifierExpressionSyntax id => (scope.Lookup(id.Name) as VariableSymbol)?.Type,
			IntegerLiteralExpressionSyntax => TypeSymbol.Int,
			DoubleLiteralExpressionSyntax => TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			MemberAccessExpressionSyntax m => CheckMemberAccessExpression(m, scope),
			BorrowExpressionSyntax b => CheckBorrowExpression(b, scope),
			StructInitializationExpressionSyntax s => CheckStructInitializationExpression(s, scope),
			HeapAllocationExpressionSyntax h => GetExpressionType(h.Expression, scope),
			IndexExpressionSyntax idx => (GetExpressionType(idx.Left, scope) as ArrayTypeSymbol)?.ElementType,
			ArrayInitializationExpressionSyntax a => CheckArrayInitialization(a, scope),
			TernaryExpressionSyntax t => CheckTernaryExpression(t, scope),
			_ => null
		};
	}

	private FunctionSymbol? ResolveFunction(string name, SymbolTable scope)
	{
		// 1. Try direct lookup (fully qualified names)
		var direct = scope.Lookup(name);
		if (direct is FunctionSymbol f) return f;

		// 2. Try lookup in current namespace
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (scope.Lookup(localMangled) is FunctionSymbol localFunc)
			return localFunc;

		// 3. Try lookup across all active 'using' namespaces in this file
		if (context.CurrentUnit is not null)
		{
			var activeUsings = new List<string>(context.CurrentUnit.Usings.Select(u => u.NamespaceName));
			if (context.CurrentUnit.NamespaceDeclaration is not null)
				activeUsings.AddRange(context.CurrentUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (scope.Lookup(candidateMangled) is FunctionSymbol match)
					return match;
			}
		}

		return null;
	}

	private string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id) return id.Name;
		if (expr is MemberAccessExpressionSyntax m) return GetBaseIdentifierName(m.Expression);
		return null;
	}
}
