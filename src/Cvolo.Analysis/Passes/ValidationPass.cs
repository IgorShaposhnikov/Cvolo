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
				{
					var isTemplate = func.GenericParameters.Count > 0 && func.GenericParameters.Any(p => context.ResolveType(p) == null);
					if (!isTemplate)
					{
						// For explicit template specializations, validate the registered monomorphized version
						if (func.GenericParameters.Count > 0)
						{
							var mangledName = context.GetMangledName(func.Name, context.CurrentNamespace);
							var instName = $"{mangledName}<{string.Join(", ", func.GenericParameters)}>";
							var instDecl = context.MonomorphizedFunctionDecls.First(d => d.Name == instName);
							CheckFunctionBody(instDecl);
						}
						else
						{
							CheckFunctionBody(func);
						}
					}
				}
				else if (member is ExtensionDeclarationSyntax extDecl)
				{
					foreach (var method in extDecl.Methods.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
					{
						CheckExtensionMethodBody(extDecl.ExtendedTypeName, method);
					}

					foreach (var ctorDecl in extDecl.Constructors)
					{
						CheckConstructorBody(extDecl.ExtendedTypeName, ctorDecl);
					}
				}
			}
		}
	}

	private void CheckConstructorBody(string extendedTypeName, ConstructorDeclarationSyntax ctor)
	{
		var extendedType = context.ResolveType(extendedTypeName) as StructTypeSymbol;
		if (extendedType is null) return;

		var wrapper = ctor.ToFunctionDeclaration();

		// Validate the body like any extension method, but "this" is always mutable:
		// a constructor's purpose is to populate fields.
		CheckExtensionMethodBody(extendedTypeName, wrapper, forceMutableThis: true);

		// Defensive Initialization: every field of 'this' must be populated before
		// the constructor exits, preventing uninitialized-memory bugs.
		var assignedFields = new HashSet<string>();
		CollectFieldAssignments(ctor.Body, assignedFields);

		foreach (var field in extendedType.Fields)
		{
			if (!assignedFields.Contains(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					ctor.Span,
					$"Defensive initialization: constructor '{extendedTypeName}' does not initialize field '{field.Name}'."
				);
			}
		}
	}

	private void CollectFieldAssignments(SyntaxNode node, HashSet<string> assigned)
	{
		if (node is BinaryExpressionSyntax bin && bin.Operator == "=")
		{
			if (bin.Left is MemberAccessExpressionSyntax member &&
				GetBaseIdentifierName(member.Expression) == "this")
			{
				// 'this.field = ...' populates the field directly
				assigned.Add(member.MemberName);
			}
			else
			{
				// Flat field assignment inside extension-member scope
				var baseName = GetBaseIdentifierName(bin.Left);
				if (baseName != null && baseName != "this")
					assigned.Add(baseName);
			}
		}

		foreach (var child in node.GetChildren())
			CollectFieldAssignments(child, assigned);
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

		// Guard: Ensure non-void functions end with a return statement
		if (func.ReturnType != "void" && !EndsWithReturn(func.Body))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				func.Span,
				$"Function '{func.Name}' is declared to return '{func.ReturnType}' but is missing a return statement."
			);
		}
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

			// Implicit Dereference: If target is value but initializer is a pointer, unwrap it
			if (initializerType is PointerTypeSymbol ptr && resolvedType is not PointerTypeSymbol)
			{
				initializerType = ptr.ReferencedType;
			}

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
			case CharacterLiteralExpressionSyntax:
				break;
			case HeapAllocationExpressionSyntax heap:
				CheckExpression(heap.Expression, scope);
				break;
			case HeapArrayAllocationExpressionSyntax heapArr:
				CheckExpression(heapArr.CountExpression, scope);
				if (GetExpressionType(heapArr.CountExpression, scope) is { } heapCountTy && !heapCountTy.Equals(TypeSymbol.Int))
				{
					context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], heapArr.CountExpression.Span, "Heap array allocation size must be an integer.");
				}
				break;
			case ArrayInitializationExpressionSyntax arrInit:
				foreach (var el in arrInit.Elements) CheckExpression(el, scope);
				break;
			case ArrayReplicationExpressionSyntax arrRepl:
				CheckExpression(arrRepl.Value, scope);
				CheckExpression(arrRepl.Count, scope);
				if (GetExpressionType(arrRepl.Count, scope) is { } countTy && !countTy.Equals(TypeSymbol.Int))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, arrRepl.Count.Span, "Array replication count must be an integer.");
				}
				break;
			case ParenthesizedStructInitializerExpressionSyntax parenStruct:
				CheckParenthesizedStructInitialization(parenStruct, scope);
				break;
			case TernaryExpressionSyntax ternary:
				CheckTernaryExpression(ternary, scope);
				break;
			case CallExpressionSyntax call:
				{
					// First evaluate argument types at the call site
					var argTypes = new List<TypeSymbol>();
					foreach (var arg in call.Arguments)
					{
						CheckExpression(arg, scope);
						var argType = GetExpressionType(arg, scope) ?? TypeSymbol.Int;
						argTypes.Add(argType);
					}

					FunctionSymbol? func = null;

					if (call.FunctionName == "sizeof")
					{
						if (call.TypeArguments.Count != 1)
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(currentFileContext, call.Span, "sizeof expects exactly 1 type argument.");
						}

						if (call.Arguments.Count != 0)
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(currentFileContext, call.Span, "sizeof does not accept value arguments.");
						}

						break;
					}

					if (call.TypeArguments.Count > 0)
					{
						var templateName = ResolveFunctionTemplateName(call.FunctionName, scope);
						if (templateName != null && context.GenericFunctionTemplates.TryGetValue(templateName, out var templateDecl))
						{
							var typeArgs = call.TypeArguments.Select(t => context.ResolveType(t)!).ToList();
							func = InstantiateGenericFunction(templateDecl, typeArgs, scope);
						}
					}
					else
					{
						// Use overload resolution logic
						func = ResolveOverloadedFunction(call.FunctionName, argTypes, scope);
					}

					if (func is null)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						var sigString = string.Join(", ", argTypes.Select(t => t.Name));
						context.Diagnostics.Report(currentFileContext, call.Span, $"No overload of function '{call.FunctionName}' matches argument types ({sigString})");
						return;
					}

					// Record the resolved overload for CodeGenerator consumption
					context.ResolvedCalls[call] = func;

					var argCount = call.Arguments.Count;
					var paramCount = func.Parameters.Count;
					var isVariadic = func.IsVariadic;

					var isExtensionCall = func.Parameters.Count > 0 && func.Parameters[0].Name == "this";
					var expectedParamCount = isExtensionCall ? paramCount - 1 : paramCount;

					if (!isVariadic && argCount != expectedParamCount)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.Span, $"Function '{call.FunctionName}' expects {expectedParamCount} arguments but received {argCount}");
						return;
					}

					if (isVariadic && argCount < paramCount)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.Span, $"Function '{call.FunctionName}' expects at least {paramCount} arguments but received {argCount}");
						return;
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
								var isMutable = varSymbol.IsMutable || (varSymbol.Type is PointerTypeSymbol ptr && ptr.IsMutable);
								if (!isMutable)
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

			// Propagate target type expectations down into parenthesized blocks recursively
			if (init.Expression is ParenthesizedStructInitializerExpressionSyntax nested)
			{
				nested.ResolvedStructTypeName = field.Type.Name;
				CheckParenthesizedStructInitialization(nested, scope);
			}
			else
			{
				CheckExpression(init.Expression, scope);
			}

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

		var isVariableMutable = false;
		if (expr.Expression is IdentifierExpressionSyntax id)
		{
			var symbol = scope.Lookup(id.Name) as VariableSymbol;
			if (symbol is not null) isVariableMutable = symbol.IsMutable;
		}

		if (expr.IsMutable && !isVariableMutable)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, "Cannot take a mutable reference (refvar) of a read-only variable.");
		}

		return new PointerTypeSymbol(innerType, expr.IsMutable);
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

		var actualType = GetExpressionType(ret.Expression, scope);
		var expectedType = context.ResolveType(currentFunc.ReturnType);

		if (actualType != null && expectedType != null)
		{
			// Implicit Dereference: If expected is value but actual returned is a pointer, unwrap it
			if (actualType is PointerTypeSymbol ptr && expectedType is not PointerTypeSymbol)
			{
				actualType = ptr.ReferencedType;
			}

			if (!actualType.Equals(expectedType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ret.Expression.Span, $"Function '{currentFunc.Name}' expects return type '{expectedType.Name}' but found '{actualType.Name}'");
			}
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
			NullLiteralExpressionSyntax n => CheckNullLiteral(n),
			StringLiteralExpressionSyntax => TypeSymbol.String,
			CharacterLiteralExpressionSyntax => TypeSymbol.Char,
			CallExpressionSyntax call when call.FunctionName == "sizeof" => TypeSymbol.Int,
			MemberAccessExpressionSyntax m => CheckMemberAccessExpression(m, scope),
			BorrowExpressionSyntax b => new PointerTypeSymbol(GetExpressionType(b.Expression, scope) ?? TypeSymbol.Int, b.IsMutable),
			StructInitializationExpressionSyntax s => CheckStructInitializationExpression(s, scope),
			HeapAllocationExpressionSyntax h => GetExpressionType(h.Expression, scope),
			HeapArrayAllocationExpressionSyntax ha => new SliceTypeSymbol(context.ResolveType(ha.ElementTypeName)!),
			IndexExpressionSyntax idx => (GetExpressionType(idx.Left, scope) as ArrayTypeSymbol)?.ElementType,
			ArrayInitializationExpressionSyntax a => CheckArrayInitialization(a, scope),
			ArrayReplicationExpressionSyntax r => CheckArrayReplication(r, scope),
			ParenthesizedStructInitializerExpressionSyntax p => p.ResolvedStructTypeName is not null ? context.ResolveType(p.ResolvedStructTypeName) : null,
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
		if (expr is BorrowExpressionSyntax b) return GetBaseIdentifierName(b.Expression);
		return null;
	}

	private string? ResolveFunctionTemplateName(string name, SymbolTable scope)
	{
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.GenericFunctionTemplates.ContainsKey(localMangled)) return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = new List<string>(context.CurrentUnit.Usings.Select(u => u.NamespaceName));
			if (context.CurrentUnit.NamespaceDeclaration is not null)
				activeUsings.AddRange(context.CurrentUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.GenericFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.GenericFunctionTemplates.ContainsKey(name)) return name;
		return null;
	}

	private FunctionSymbol InstantiateGenericFunction(FunctionDeclarationSyntax templateDecl, List<TypeSymbol> typeArgs, SymbolTable scope)
	{
		// Resolve the template's fully qualified mangled name (e.g. BankSystem.IO.PrintAccountInfo)
		var templateMangledName = ResolveFunctionTemplateName(templateDecl.Name, scope)!;
		var rawName = $"{templateMangledName}<{string.Join(",", typeArgs.Select(t => t.Name))}>";
		// Canonical Name
		var instName = context.NormalizeGenericName(rawName);

		if (context.MonomorphizedFunctions.TryGetValue(instName, out var existing))
			return existing;

		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < templateDecl.GenericParameters.Count; i++)
		{
			substitutionMap[templateDecl.GenericParameters[i]] = typeArgs[i];
		}

		TypeSymbol ResolveSubstitutedType(string typeName)
		{
			// Substitute placeholders inside type name strings first
			var substitutedTypeName = typeName;
			foreach (var kv in substitutionMap)
			{
				substitutedTypeName = System.Text.RegularExpressions.Regex.Replace(substitutedTypeName, $@"\b{kv.Key}\b", kv.Value.Name);
			}

			if (substitutedTypeName.StartsWith("refvar ") || substitutedTypeName.StartsWith("ref "))
			{
				var isMutable = substitutedTypeName.StartsWith("refvar ");
				var innerName = isMutable ? substitutedTypeName.Substring(7) : substitutedTypeName.Substring(4);
				var innerType = ResolveSubstitutedType(innerName);
				return new PointerTypeSymbol(innerType, isMutable);
			}

			return context.ResolveType(substitutedTypeName)!;
		}

		var returnType = ResolveSubstitutedType(templateDecl.ReturnType);
		var parameters = new List<ParameterSymbol>();
		var instParameters = new List<ParameterSyntax>();

		foreach (var param in templateDecl.Parameters)
		{
			var paramType = ResolveSubstitutedType(param.Type);
			parameters.Add(new ParameterSymbol(param.Name, paramType));
			instParameters.Add(new ParameterSyntax(param.Span, paramType.Name, param.Name));
		}

		var instSymbol = new FunctionSymbol(instName, returnType, parameters);
		context.MonomorphizedFunctions[instName] = instSymbol;

		var instBody = SubstituteBlockGenerics(templateDecl.Body, substitutionMap);
		var instDecl = new FunctionDeclarationSyntax(templateDecl.Span, returnType.Name, instName, [], instParameters, instBody);

		context.MonomorphizedFunctionDecls.Add(instDecl);

		// Map the monomorphized instance to the template's original file unit
		if (context.SymbolUnits.TryGetValue(templateMangledName, out var templateUnit))
		{
			context.SymbolUnits[instName] = templateUnit;
		}

		// Bind the newly generated function body immediately!
		var localScope = new SymbolTable(context.Globals);
		foreach (var p in parameters)
		{
			localScope.Declare(new VariableSymbol(p.Name, p.Type, false) { IsInitialized = true });
		}

		CheckBlock(instBody, localScope, instDecl);

		return instSymbol;
	}

	private BlockStatementSyntax SubstituteBlockGenerics(BlockStatementSyntax block, Dictionary<string, TypeSymbol> substitutionMap)
	{
		var statements = new List<SyntaxNode>();
		foreach (var stmt in block.Statements)
			statements.Add(SubstituteStatementGenerics(stmt, substitutionMap));
		return new BlockStatementSyntax(block.Span, statements);
	}

	private SyntaxNode SubstituteStatementGenerics(SyntaxNode stmt, Dictionary<string, TypeSymbol> substitutionMap)
	{
		switch (stmt)
		{
			case VariableDeclarationSyntax v:
				var newType = v.Type;
				if (newType != null)
				{
					foreach (var kv in substitutionMap)
					{
						newType = System.Text.RegularExpressions.Regex.Replace(newType, $@"\b{kv.Key}\b", kv.Value.Name);
					}
				}
				return new VariableDeclarationSyntax(v.Span, v.IsMutable, newType, v.Name, v.Initializer != null ? SubstituteExpressionGenerics(v.Initializer, substitutionMap) : null);

			case BlockStatementSyntax b:
				return SubstituteBlockGenerics(b, substitutionMap);

			case IfStatementSyntax i:
				return new IfStatementSyntax(i.Span, SubstituteExpressionGenerics(i.Condition, substitutionMap), SubstituteStatementGenerics(i.ThenStatement, substitutionMap), i.ElseClause != null ? new ElseClauseSyntax(i.ElseClause.Span, SubstituteBlockGenerics(i.ElseClause.Body, substitutionMap)) : null);

			case WhileStatementSyntax w:
				return new WhileStatementSyntax(w.Span, SubstituteExpressionGenerics(w.Condition, substitutionMap), SubstituteStatementGenerics(w.Body, substitutionMap));

			case ForStatementSyntax f:
				return new ForStatementSyntax(f.Span, SubstituteStatementGenerics(f.Initializer, substitutionMap) as VariableDeclarationSyntax ?? f.Initializer, SubstituteExpressionGenerics(f.Condition, substitutionMap), SubstituteExpressionGenerics(f.Increment, substitutionMap), SubstituteStatementGenerics(f.Body, substitutionMap));

			case ReturnStatementSyntax r:
				return new ReturnStatementSyntax(r.Span, r.Expression != null ? SubstituteExpressionGenerics(r.Expression, substitutionMap) : null);

			case ExpressionStatementSyntax e:
				return new ExpressionStatementSyntax(e.Span, SubstituteExpressionGenerics(e.Expression, substitutionMap));

			default:
				return stmt;
		}
	}

	private ExpressionSyntax SubstituteExpressionGenerics(ExpressionSyntax expr, Dictionary<string, TypeSymbol> substitutionMap)
	{
		switch (expr)
		{
			case BinaryExpressionSyntax bin:
				return new BinaryExpressionSyntax(bin.Span, SubstituteExpressionGenerics(bin.Left, substitutionMap), bin.Operator, SubstituteExpressionGenerics(bin.Right, substitutionMap));

			case UnaryExpressionSyntax unary:
				var newOp = unary.Operator;
				if (newOp.StartsWith("(") && newOp.EndsWith(")"))
				{
					foreach (var kv in substitutionMap)
					{
						newOp = System.Text.RegularExpressions.Regex.Replace(newOp, $@"\b{kv.Key}\b", kv.Value.Name);
					}
				}

				return new UnaryExpressionSyntax(unary.Span, newOp, SubstituteExpressionGenerics(unary.Operand, substitutionMap));

			case CallExpressionSyntax call:
				var newTypeArgs = call.TypeArguments.Select(t => {
					var substituted = t;
					foreach (var kv in substitutionMap)
					{
						substituted = System.Text.RegularExpressions.Regex.Replace(substituted, $@"\b{kv.Key}\b", kv.Value.Name);
					}

					return substituted;
				}).ToList();
				var newArgs = call.Arguments.Select(a => SubstituteExpressionGenerics(a, substitutionMap)).ToList();
				return new CallExpressionSyntax(call.Span, call.FunctionName, newTypeArgs, newArgs);

			case StructInitializationExpressionSyntax structInit:
				var newTypeName = structInit.StructTypeName;
				foreach (var kv in substitutionMap)
				{
					newTypeName = System.Text.RegularExpressions.Regex.Replace(newTypeName, $@"\b{kv.Key}\b", kv.Value.Name);
				}

				var newInits = structInit.Initializers.Select(i => new MemberInitializerSyntax(i.Span, i.MemberName, SubstituteExpressionGenerics(i.Expression, substitutionMap))).ToList();
				return new StructInitializationExpressionSyntax(structInit.Span, newTypeName, newInits);

			case MemberAccessExpressionSyntax m:
				return new MemberAccessExpressionSyntax(m.Span, SubstituteExpressionGenerics(m.Expression, substitutionMap), m.MemberName);

			case IndexExpressionSyntax idx:
				return new IndexExpressionSyntax(idx.Span, SubstituteExpressionGenerics(idx.Left, substitutionMap), SubstituteExpressionGenerics(idx.Index, substitutionMap));

			case BorrowExpressionSyntax b:
				return new BorrowExpressionSyntax(b.Span, SubstituteExpressionGenerics(b.Expression, substitutionMap), b.IsMutable);

			case HeapAllocationExpressionSyntax h:
				return new HeapAllocationExpressionSyntax(h.Span, SubstituteExpressionGenerics(h.Expression, substitutionMap));

			case ArrayInitializationExpressionSyntax arr:
				return new ArrayInitializationExpressionSyntax(arr.Span, arr.Elements.Select(e => SubstituteExpressionGenerics(e, substitutionMap)).ToList());

			case TernaryExpressionSyntax t:
				return new TernaryExpressionSyntax(t.Span, SubstituteExpressionGenerics(t.Condition, substitutionMap), SubstituteExpressionGenerics(t.ThenExpression, substitutionMap), SubstituteExpressionGenerics(t.ElseExpression, substitutionMap));

			default:
				return expr;
		}
	}

	private static bool EndsWithReturn(SyntaxNode s) => s switch
	{
		BlockStatementSyntax b => b.Statements.Count > 0 && b.Statements[^1] is ReturnStatementSyntax,
		ReturnStatementSyntax => true,
		_ => false,
	};

	private FunctionSymbol? ResolveOverloadedFunction(string name, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope)
	{
		var candidates = new List<FunctionSymbol>();
		var baseName = name;
		var adjustedArgTypes = new List<TypeSymbol>(argTypes);

		// Detect dotted extension method call
		if (name.Contains('.'))
		{
			var parts = name.Split('.');
			var receiverName = parts[0];
			var methodName = parts[1];

			if (scope.Lookup(receiverName) is VariableSymbol receiverSymbol)
			{
				var structType = receiverSymbol.Type as StructTypeSymbol;
				if (structType is null && receiverSymbol.Type is PointerTypeSymbol ptr)
					structType = ptr.ReferencedType as StructTypeSymbol;

				if (structType is not null)
				{
					// Resolve using "Point.Move" as the base name
					baseName = $"{structType.Name}.{methodName}";

					// Prepend the receiver's reference type as the first argument!
					var receiverRefType = new PointerTypeSymbol(structType, isMutable: receiverSymbol.IsMutable);
					adjustedArgTypes.Insert(0, receiverRefType);
				}
			}
		}
		else if (context.Constructors.ContainsKey(name))
		{
			// Bare constructor call 'T(args)': prepend the implicit destination-storage
			// receiver so ctor candidates match like extension methods.
			if (context.StructTypes.TryGetValue(name, out var ctorStruct))
				adjustedArgTypes.Insert(0, new PointerTypeSymbol(ctorStruct, isMutable: true));
		}

		// 1. Gather all candidates matching the resolved base name
		GatherOverloadCandidates(baseName, candidates);

		// 2. Select the candidate with the best signature match score
		FunctionSymbol? bestMatch = null;
		var bestScore = -1;

		foreach (var candidate in candidates)
		{
			var paramTypes = candidate.Parameters.Select(p => p.Type).ToList();
			var score = CompareSignature(paramTypes, adjustedArgTypes, candidate.IsVariadic);
			if (score > bestScore)
			{
				bestScore = score;
				bestMatch = candidate;
			}
		}

		return bestScore >= 0 ? bestMatch : null;
	}

	private void GatherOverloadCandidates(string name, List<FunctionSymbol> targetList)
	{
		// Direct or exact match search (e.g., "MyNamespace.Point.Move" or "Point.Move")
		if (context.OverloadedFunctions.TryGetValue(name, out var directMatches))
			targetList.AddRange(directMatches);

		// Scoped Namespace lookup
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.OverloadedFunctions.TryGetValue(localMangled, out var localMatches))
			targetList.AddRange(localMatches);

		// Search through imported namespaces
		if (context.CurrentUnit is not null)
		{
			var activeUsings = new List<string>(context.CurrentUnit.Usings.Select(u => u.NamespaceName));
			if (context.CurrentUnit.NamespaceDeclaration is not null)
				activeUsings.AddRange(context.CurrentUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.OverloadedFunctions.TryGetValue(candidateMangled, out var match))
					targetList.AddRange(match);
			}
		}
	}

	private int CompareSignature(IReadOnlyList<TypeSymbol> paramTypes, IReadOnlyList<TypeSymbol> argTypes, bool isVariadic)
	{
		if (!isVariadic && paramTypes.Count != argTypes.Count)
			return -1; // Incompatible bounds

		if (isVariadic && argTypes.Count < paramTypes.Count)
			return -1; // Missing required parameters

		var score = 0;
		var countToCheck = paramTypes.Count;

		for (var i = 0; i < countToCheck; i++)
		{
			var param = paramTypes[i];
			var arg = argTypes[i];

			if (param.Equals(arg))
			{
				score += 4; // Direct exact match is preferred
			}
			else if (param is PointerTypeSymbol paramPtr && arg is PointerTypeSymbol argPointer &&
					 paramPtr.ReferencedType is SliceTypeSymbol sliceType && argPointer.ReferencedType is ArrayTypeSymbol arrayType &&
					 sliceType.ElementType.Equals(arrayType.ElementType))
			{
				// Safety check: Cannot pass a read-only pointer to a mutating parameter!
				if (paramPtr.IsMutable && !argPointer.IsMutable)
				{
					return -1;
				}

				score += paramPtr.IsMutable == argPointer.IsMutable ? 3 : 2;
				continue;
			}
			else if (param is PointerTypeSymbol pPtr && arg is PointerTypeSymbol aPtr && pPtr.ReferencedType.Equals(aPtr.ReferencedType))
			{
				if (pPtr.IsMutable && !aPtr.IsMutable)
				{
					return -1;
				}

				score += pPtr.IsMutable == aPtr.IsMutable ? 3 : 2;
				continue;
			}
			else if (param is SliceTypeSymbol slice && arg is ArrayTypeSymbol arr && slice.ElementType.Equals(arr.ElementType))
			{
				score += 3; // Implicit decay of Array to Dynamic Slice
			}
			else if (param is PointerTypeSymbol ptr && ptr.ReferencedType.Equals(arg))
			{
				score += 2; // Implicit reference casting
			}
			else if (arg is PointerTypeSymbol argPtr && param.Equals(argPtr.ReferencedType))
			{
				score += 2; // Implicit dereference matches
			}
			else if (param.Equals(TypeSymbol.Double) && arg.Equals(TypeSymbol.Int))
			{
				score += 1; // Implicit numeric promotion (int -> double)
			}
			else if (param.Name == "string" && ((arg is ArrayTypeSymbol arrSymbol && arrSymbol.ElementType.Name == "char") || (arg is SliceTypeSymbol sliceSymbol && sliceSymbol.ElementType.Name == "char")))
			{
				score += 1; // Implicit char array/slice to string decay
			}
			else
			{
				return -1; // Parameter signature mismatch
			}
		}

		if (isVariadic) score += 1;

		return score;
	}

	private TypeSymbol? CheckArrayReplication(ArrayReplicationExpressionSyntax expr, SymbolTable scope)
	{
		var valueType = GetExpressionType(expr.Value, scope) ?? TypeSymbol.Int;
		if (expr.Count is IntegerLiteralExpressionSyntax countLit)
		{
			return new ArrayTypeSymbol(valueType, countLit.Value);
		}

		return new ArrayTypeSymbol(valueType, 0);
	}

	private void CheckParenthesizedStructInitialization(ParenthesizedStructInitializerExpressionSyntax expr, SymbolTable scope)
	{
		var type = context.ResolveType(expr.ResolvedStructTypeName!);
		if (type is not StructTypeSymbol structType) return;

		foreach (var init in expr.Initializers)
		{
			var field = structType.FindField(init.MemberName);
			if (field is null) continue;

			if (init.Expression is ParenthesizedStructInitializerExpressionSyntax nestedSub)
			{
				nestedSub.ResolvedStructTypeName = field.Type.Name;
				CheckParenthesizedStructInitialization(nestedSub, scope);
			}
			else
			{
				CheckExpression(init.Expression, scope);
			}
		}
	}

	private void CheckExtensionMethodBody(string extendedTypeName, FunctionDeclarationSyntax method, bool forceMutableThis = false)
	{
		var extendedType = context.ResolveType(extendedTypeName) as StructTypeSymbol;
		if (extendedType is null) return;

		// 1. Static AST Mutation Scan: Infer if the method modifies any fields
		var isMutating = forceMutableThis || DetectFieldMutation(method.Body, extendedType);

		// 2. Locate the registered function symbol using the unmutated base registration
		var baseMangledName = context.GetMangledName($"{extendedTypeName}.{method.Name}", context.CurrentNamespace);
		var lookupThisType = new PointerTypeSymbol(extendedType, isMutable: false); // Must use 'false' to match DeclarationPass
		var lookupParams = new List<TypeSymbol> { lookupThisType };
		foreach (var p in method.Parameters)
		{
			lookupParams.Add(context.ResolveType(p.Type)!);
		}

		var lookupOverloadedName = context.GetOverloadedMangledName(baseMangledName, lookupParams);
		var funcSymbol = context.Globals.Lookup(lookupOverloadedName) as FunctionSymbol;

		if (funcSymbol is not null)
		{
			// Upgrade the registered symbol's "this" parameter mutability based on our scan!
			var thisParamType = funcSymbol.Parameters[0].Type as PointerTypeSymbol;
			if (thisParamType is not null)
			{
				thisParamType.IsMutable = isMutating;
			}
		}

		// 3. Populate local scope with fields so they can be written as flat local variables
		var localScope = new SymbolTable(context.Globals);
		foreach (var field in extendedType.Fields)
		{
			localScope.Declare(new VariableSymbol(field.Name, field.Type, isMutable: true) { IsInitialized = true });
		}

		foreach (var param in method.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is not null)
			{
				localScope.Declare(new VariableSymbol(param.Name, paramType, isMutable: false) { IsInitialized = true });
			}
		}

		CheckBlock(method.Body, localScope, method);
	}

	private readonly HashSet<NullLiteralExpressionSyntax> _checkedNullLiterals = new();

	private TypeSymbol CheckNullLiteral(NullLiteralExpressionSyntax expr)
	{
		// GetExpressionType is consulted from several check paths; report each null
		// literal only once regardless of how many times its type is inferred.
		if (_checkedNullLiterals.Add(expr))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, "The 'null' literal requires a pointer type and is not valid in safe code.");
		}

		return TypeSymbol.Int;
	}

	private bool DetectFieldMutation(SyntaxNode node, StructTypeSymbol structType)
	{
		if (node is BinaryExpressionSyntax bin && bin.Operator == "=")
		{
			var baseName = GetBaseIdentifierName(bin.Left);
			if (baseName != null && structType.FindField(baseName) != null)
			{
				return true; // Detected a field assignment!
			}
		}

		foreach (var child in node.GetChildren())
		{
			if (DetectFieldMutation(child, structType))
				return true;
		}

		return false;
	}
}
