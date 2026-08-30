using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes;

public sealed class ValidationPass(BindingContext context)
{
	private ClassificationAnalyzer? _classification;
	private ClassificationAnalyzer Classification => _classification ??= new ClassificationAnalyzer(context);
	private int _unsafeDepth;

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

					// Interface-parameterized functions are implicit generic templates: their bodies are
					// validated at each call site (monomorphized), never here with an abstract interface type.
					var ifaceTemplateName = context.GetMangledName(func.Name, context.CurrentNamespace);
					var isInterfaceTemplate = context.InterfaceFunctionTemplates.ContainsKey(ifaceTemplateName);

					if (isInterfaceTemplate) continue;

					// Protocol-parameterized functions are likewise implicit templates: a protocol name has
					// no value representation, so the body is validated when monomorphized at a call site.
					var isProtocolTemplate = context.ProtocolFunctionTemplates.ContainsKey(ifaceTemplateName);

					if (isProtocolTemplate) continue;

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
					var extendedType = context.ResolveType(extDecl.ExtendedTypeName);
					if (extendedType != null && (context.GenericStructTemplates.ContainsKey(extendedType.Name) || context.GenericUnionTemplates.ContainsKey(extendedType.Name)))
					{
						continue;
					}

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

		// Validate monomorphized extension methods and constructors
		var validatedMonomorphized = new HashSet<string>();
		while (true)
		{
			var pending = context.MonomorphizedExtensionDecls.Where(d => {
				var name = context.MonomorphizedExtensionNames[d];
				return !validatedMonomorphized.Contains(name);
			}).ToList();

			if (pending.Count == 0) break;

			foreach (var decl in pending)
			{
				var emitName = context.MonomorphizedExtensionNames[decl];
				validatedMonomorphized.Add(emitName);

				if (context.SymbolUnits.TryGetValue(emitName, out var unit))
				{
					context.CurrentUnit = unit;
					context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
				}

				var extendedTypeName = context.MonomorphizedExtensionExtendedTypes[emitName];

				if (decl is FunctionDeclarationSyntax func)
				{
					CheckExtensionMethodBody(extendedTypeName, func);
				}
				else if (decl is ConstructorDeclarationSyntax ctor)
				{
					CheckConstructorBody(extendedTypeName, ctor);
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
		var baseUnsafeDepth = _unsafeDepth;
		_unsafeDepth = IsUnsafeFunction(func) ? 1 : 0;
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

		_unsafeDepth = baseUnsafeDepth;
	}

	private static bool IsUnsafeFunction(FunctionDeclarationSyntax func) =>
		func.Modifier == SafetyTier.Unsafe ||
		func.Attributes.Any(static a => string.Equals(a.Name, "UnsafeBody", StringComparison.OrdinalIgnoreCase));

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
			case UnsafeBlockStatementSyntax unsafeBlock:
				_unsafeDepth++;
				CheckBlock(unsafeBlock.Body, new SymbolTable(scope), currentFunc);
				_unsafeDepth--;
				break;
			case SwitchStatementSyntax sw:
				CheckSwitchStatement(sw, scope, currentFunc);
				break;
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
				var isValidNull = initializerType.Equals(TypeSymbol.Null) &&
								  (resolvedType is RawPointerTypeSymbol ||
								  (resolvedType is UnionTypeSymbol union && union.IsOption));

				// Integer width family: implicit conversion between exact-width integers
				// (byte/short/int/long and unsigned variants) is allowed.
				var isIntegerWidthConversion = TypeSymbol.IsNumericIntegerType(resolvedType)
					&& TypeSymbol.IsNumericIntegerType(initializerType);

				if (!isValidNull && !isIntegerWidthConversion)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					if (initializerType.Equals(TypeSymbol.Null))
					{
						context.Diagnostics.Report(currentFileContext, varDecl.Span, "The 'null' literal requires a pointer type (Option or raw pointer).");
					}
					else
					{
						context.Diagnostics.Report(currentFileContext, varDecl.Span, $"Cannot initialize variable of type '{resolvedType.Name}' with value of type '{initializerType.Name}'");
					}
				}
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
						// Reconstruct and resolve the struct/union instantiation name to check if this is a generic constructor call
						var structNameWithArgs = $"{call.FunctionName}<{string.Join(", ", call.TypeArguments)}>";
						var resolvedType = context.ResolveType(structNameWithArgs);

						if (resolvedType is StructTypeSymbol or UnionTypeSymbol)
						{
							// This is a generic constructor call! Use the fully qualified resolved type name for overload resolution
							func = ResolveOverloadedFunction(resolvedType.Name, argTypes, scope, call);
						}
						else
						{
							// Fallback to standard generic function monomorphization
							var templateName = ResolveFunctionTemplateName(call.FunctionName, scope);
							if (templateName != null && context.GenericFunctionTemplates.TryGetValue(templateName, out var templateDecl))
							{
								var typeArgs = call.TypeArguments.Select(t => context.ResolveType(t)!).ToList();
								func = InstantiateGenericFunction(templateDecl, typeArgs, scope);
							}
						}
					}
					else
					{
						// Use overload resolution logic for standard non-generic functions / constructors
						func = ResolveOverloadedFunction(call.FunctionName, argTypes, scope);

						// No concrete overload matched: fall back to interface-parameterized dispatch
						// (implicit generic templates monomorphized with the concrete conforming arg types).
						if (func is null && ResolveInterfaceFunctionTemplateName(call.FunctionName, scope) is not null)
						{
							// The callee is an interface template: specific conformance/arg-count
							// diagnostics are reported inside. Return early so the generic
							// "no overload" message is not also emitted.
							func = TryResolveInterfaceCall(call, argTypes, scope);
							if (func is null) return;
						}

						// No concrete overload matched: fall back to protocol-parameterized
						// dispatch (structural duck typing against the protocol's canonical
						// member tokens; monomorphized with the structurally conforming arg types).
						if (func is null && ResolveProtocolFunctionTemplateName(call.FunctionName, scope) is not null)
						{
							// The callee is a protocol template: specific structural-conformance/
							// arg-count diagnostics are reported inside. Return early so the
							// generic "no overload" message is not also emitted.
							func = TryResolveProtocolCall(call, argTypes, scope);
							if (func is null) return;
						}
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

					if (!isVariadic)
					{
						for (var i = 0; i < call.Arguments.Count; i++)
						{
							var paramIndex = isExtensionCall ? i + 1 : i;
							if (paramIndex >= func.Parameters.Count) break;
							CheckLargeUnionByValueArgument(call.Arguments[i], func.Parameters[paramIndex].Type, scope);
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
							var isMutable = varSymbol.IsMutable || (varSymbol.Type is PointerTypeSymbol ptr && ptr.IsMutable);
							if (!isMutable)
							{
								var currentFileContext = context.FileContexts[context.CurrentUnit!];
								context.Diagnostics.Report(currentFileContext, id.Span, $"Cannot assign to immutable variable '{id.Name}'");
							}

							CheckEnumIntMismatch(varSymbol.Type, GetExpressionType(bin.Right, scope), bin.Span);
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
				else
				{
					CheckExpression(bin.Left, scope);
					CheckExpression(bin.Right, scope);
					CheckEnumIntMismatch(GetExpressionType(bin.Left, scope), GetExpressionType(bin.Right, scope), bin.Span);
				}

				break;
			}
			case UnaryExpressionSyntax unary:
				CheckExpression(unary.Operand, scope);
				CheckUnaryCast(unary, scope);
				break;
			case VoidLiteralExpressionSyntax:
				break;
		}
	}

	private TypeSymbol? CheckMemberAccessExpression(MemberAccessExpressionSyntax expr, SymbolTable scope)
	{
		// Enum scoped-variant access: EnumName.Variant (optionally namespaced). The
		// receiver is a *type name*, not a value expression — resolve it before the
		// scope lookup so the receiver is not reported as an undefined variable.
		if (TryResolveEnumVariantReceiver(expr) is { } enumType)
		{
			var variant = enumType.FindVariant(expr.MemberName);
			if (variant is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span,
					$"Enum '{enumType.Name}' does not contain variant '{expr.MemberName}'");
				return null;
			}

			return enumType;
		}

		CheckExpression(expr.Expression, scope);
		var leftType = GetExpressionType(expr.Expression, scope);
		if (leftType is null) return null;

		if (leftType is PointerTypeSymbol pointerType)
		{
			leftType = pointerType.ReferencedType;
		}

		if (leftType.Name.EndsWith("[]") && expr.MemberName == "Length")
		{
			return TypeSymbol.Int;
		}

		if (leftType is EnumTypeSymbol)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span,
				$"Type '{leftType.Name}' is an enum; only scoped variant access ('{leftType.Name}.VariantName') is allowed.");
			return null;
		}

		if (leftType is UnionTypeSymbol unionType)
		{
			var variantField = unionType.FindField(expr.MemberName);
			if (variantField is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span, $"Union '{unionType.Name}' does not contain variant '{expr.MemberName}'");
				return null;
			}
			return variantField.Type;
		}

		if (leftType is not StructTypeSymbol structType)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Type '{leftType.Name}' is not a struct or union; cannot access member '{expr.MemberName}'");
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

	/// <summary>
	/// Resolves an enum type name used as a scoped-variant-access receiver
	/// (e.g. the 'Status' in 'Status.Active', possibly namespaced). Returns null
	/// when the receiver is a value expression rather than an enum type name.
	/// </summary>
	private EnumTypeSymbol? TryResolveEnumVariantReceiver(MemberAccessExpressionSyntax m)
	{
		var dotted = GetDottedName(m.Expression);
		if (dotted is null)
			return null;

		return context.ResolveType(dotted) as EnumTypeSymbol;
	}

	private static string? GetDottedName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
			return id.Name;
		if (expr is MemberAccessExpressionSyntax m && GetDottedName(m.Expression) is { } baseName)
			return $"{baseName}.{m.MemberName}";
		return null;
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

		if (type is UnionTypeSymbol unionType)
		{
			if (expr.Initializers.Count != 1)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span, $"Union initialization of '{unionType.Name}' must specify exactly one variant.");
				return unionType;
			}

			var init = expr.Initializers[0];
			var field = unionType.FindField(init.MemberName);
			if (field is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span, $"Union '{unionType.Name}' does not contain variant '{init.MemberName}'");
				return unionType;
			}

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
				var isValidNull = initType.Equals(TypeSymbol.Null) &&
								  (field.Type is RawPointerTypeSymbol ||
								  (field.Type is UnionTypeSymbol union && union.IsOption));

				if (!isValidNull)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					if (initType.Equals(TypeSymbol.Null))
					{
						context.Diagnostics.Report(currentFileContext, init.Span, "The 'null' literal requires a pointer type (Option or raw pointer).");
					}
					else
					{
						context.Diagnostics.Report(currentFileContext, init.Span, $"Cannot initialize field '{init.MemberName}' of type '{field.Type.Name}' with value of type '{initType.Name}'");
					}
				}
			}

			return unionType;
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

			// >16B union value-passing restriction (Memory spec §6 Rule 5)
			if (expectedType is UnionTypeSymbol retUnion && !retUnion.IsNpoEligible &&
				Classification.CalculateByteSize(retUnion) > 16)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ret.Expression.Span,
					$"Union '{retUnion.Name}' is {Classification.CalculateByteSize(retUnion)} bytes. Returning by value is forbidden for unions larger than 16 bytes; return a 'ref'/'refvar' instead.");
				return;
			}

			if (!actualType.Equals(expectedType))
			{
				var isValidNull = actualType.Equals(TypeSymbol.Null) &&
								  (expectedType is RawPointerTypeSymbol ||
								  (expectedType is UnionTypeSymbol union && union.IsOption));

				if (!isValidNull)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					if (actualType.Equals(TypeSymbol.Null))
					{
						context.Diagnostics.Report(currentFileContext, ret.Expression.Span, "The 'null' literal requires a pointer type (Option or raw pointer).");
					}
					else
					{
						context.Diagnostics.Report(currentFileContext, ret.Expression.Span, $"Function '{currentFunc.Name}' expects return type '{expectedType.Name}' but found '{actualType.Name}'");
					}
				}
			}
		}
	}

	private TypeSymbol? GetExpressionType(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			IdentifierExpressionSyntax id => (scope.Lookup(id.Name) as VariableSymbol)?.Type,
			IntegerLiteralExpressionSyntax intLit => intLit.Value is > int.MaxValue or < int.MinValue ? TypeSymbol.Long : TypeSymbol.Int,
			DoubleLiteralExpressionSyntax => TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			NullLiteralExpressionSyntax => TypeSymbol.Null,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			CharacterLiteralExpressionSyntax => TypeSymbol.Char,
			CallExpressionSyntax call when call.FunctionName == "sizeof" => TypeSymbol.Int,
			CallExpressionSyntax call => context.ResolvedCalls.TryGetValue(call, out var resolved) ? resolved.ReturnType : null,
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
			VoidLiteralExpressionSyntax => TypeSymbol.Void,
			UnaryExpressionSyntax unary => GetUnaryExpressionType(unary, scope),
			_ => null
		};
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
				substitutedTypeName = SubstituteTypeToken(substitutedTypeName, kv.Key, kv.Value.Name);
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
		var instDecl = new FunctionDeclarationSyntax(templateDecl.Span, returnType.Name, instName, [], instParameters, instBody, modifier: templateDecl.Modifier);

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
			localScope.Declare(new VariableSymbol(p.Name, p.Type, p.Type is PointerTypeSymbol { IsMutable: true }) { IsInitialized = true });
		}

		CheckBlock(instBody, localScope, instDecl);

		return instSymbol;
	}

	// ---------------------------------------------------------------------------
	// Interface-parameterized (implicit generic) function dispatch.
	// A function with a nominal-interface-typed parameter is lowered to a template
	// and monomorphized at each call site with the concrete conforming argument
	// type (static-only dispatch; no vtable / fat pointers).
	// ---------------------------------------------------------------------------

	private string? ResolveInterfaceFunctionTemplateName(string name, SymbolTable scope)
	{
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.InterfaceFunctionTemplates.ContainsKey(localMangled)) return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = new List<string>(context.CurrentUnit.Usings.Select(u => u.NamespaceName));
			if (context.CurrentUnit.NamespaceDeclaration is not null)
				activeUsings.AddRange(context.CurrentUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.InterfaceFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.InterfaceFunctionTemplates.ContainsKey(name)) return name;
		return null;
	}

	private bool ConformsToInterface(TypeSymbol type, InterfaceTypeSymbol iface)
	{
		var baseType = type is PointerTypeSymbol ptr ? ptr.ReferencedType : type;
		return context.Conformance.TryGetValue(baseType.Name, out var ifaces) && ifaces.Contains(iface.Name);
	}

	/// <summary>
	/// Resolves a call to an interface-parameterized function by monomorphizing the
	/// template with the concrete conforming argument types. When the callee is an
	/// interface template but the call cannot be instantiated, reports the specific
	/// id-less diagnostic (conformance / argument-count / conflicting-concrete) and
	/// returns null so the caller suppresses the generic "no overload" message.
	/// </summary>
	private FunctionSymbol? TryResolveInterfaceCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope)
	{
		var templateName = ResolveInterfaceFunctionTemplateName(call.FunctionName, scope);
		if (templateName is null) return null;

		var templateDecl = context.InterfaceFunctionTemplates[templateName];
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		if (argTypes.Count != templateDecl.Parameters.Count)
		{
			context.Diagnostics.Report(currentFileContext, call.Span,
				$"Function '{call.FunctionName}' expects {templateDecl.Parameters.Count} argument(s) but received {argTypes.Count}");
			return null;
		}

		// Build the substitution map: each interface-typed parameter maps to its concrete arg type.
		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < templateDecl.Parameters.Count; i++)
		{
			var param = templateDecl.Parameters[i];

			// Unwrap an optional ref/refvar prefix to discover the underlying interface name.
			var isRefParam = param.Type.StartsWith("refvar ", StringComparison.Ordinal)
				|| param.Type.StartsWith("ref ", StringComparison.Ordinal);
			var interfaceTypeName = isRefParam
				? (param.Type.StartsWith("refvar ", StringComparison.Ordinal) ? param.Type[7..] : param.Type[4..])
				: param.Type;

			if (context.ResolveType(interfaceTypeName) is not InterfaceTypeSymbol iface) continue;

			// For a ref/refvar interface parameter, the concrete argument arrives as a pointer;
			// substitute the referent type name so the ref/refvar wrapper is supplied by the
			// template's own parameter string (e.g. "refvar IShape" -> "refvar Rect").
			var concreteArg = argTypes[i];
			var concrete = concreteArg is PointerTypeSymbol cPtr ? cPtr.ReferencedType : concreteArg;

			// An argument that is itself interface-typed has no concrete representation to
			// monomorphize against (the interface is an abstract marker, not a value type).
			// Report a dedicated unresolved-concrete-type diagnostic instead of the misleading
			// "does not conform" message.
			if (concrete is InterfaceTypeSymbol)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Interface parameter '{param.Name}' of function '{call.FunctionName}' cannot be resolved to a concrete conforming type; argument is abstract interface type '{concrete.Name}'");
				return null;
			}

			if (!ConformsToInterface(concrete, iface))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not conform to interface '{iface.Name}' for parameter '{param.Name}'");
				return null;
			}

			if (substitutionMap.TryGetValue(interfaceTypeName, out var existing) && existing.Name != concrete.Name)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Interface parameter '{param.Name}' requires a single concrete type, but both '{existing.Name}' and '{concrete.Name}' were passed");
				return null;
			}

			substitutionMap[interfaceTypeName] = concrete;
		}

		if (substitutionMap.Count == 0) return null;

		return InstantiateInterfaceFunction(templateDecl, substitutionMap, scope);
	}

	private FunctionSymbol InstantiateInterfaceFunction(
		FunctionDeclarationSyntax templateDecl, Dictionary<string, TypeSymbol> substitutionMap, SymbolTable scope)
	{
		var templateMangledName = ResolveInterfaceFunctionTemplateName(templateDecl.Name, scope)!;
		return InstantiateDispatchFunction(templateDecl, substitutionMap, scope, templateMangledName);
	}

	private FunctionSymbol InstantiateProtocolFunction(
		FunctionDeclarationSyntax templateDecl, Dictionary<string, TypeSymbol> substitutionMap, SymbolTable scope)
	{
		var templateMangledName = ResolveProtocolFunctionTemplateName(templateDecl.Name, scope)!;
		return InstantiateDispatchFunction(templateDecl, substitutionMap, scope, templateMangledName);
	}

	/// <summary>
	/// Shared monomorphization core for interface/protocol-parameterized templates:
	/// substitutes the concrete conforming type names into the signature and body,
	/// registers the instance, and validates the body once with the concrete types.
	/// </summary>
	private static string SubstituteTypeToken(string text, string key, string value)
		{
			// A generic-instantiated key (e.g. "IContainer<int>") can never be matched by a
			// \b...\b regex pattern (a word boundary cannot be asserted after a non-word '>'),
			// so generic keys are substituted as exact type tokens instead.
			if (key.Contains('<'))
				return text.Replace(key, value);

			return System.Text.RegularExpressions.Regex.Replace(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(key)}\b", value);
		}

		private FunctionSymbol InstantiateDispatchFunction(
		FunctionDeclarationSyntax templateDecl, Dictionary<string, TypeSymbol> substitutionMap, SymbolTable scope,
		string templateMangledName)
	{
		var rawName = $"{templateMangledName}<{string.Join(",", substitutionMap.Values.Select(t => t.Name))}>";
		var instName = context.NormalizeGenericName(rawName);

		if (context.MonomorphizedFunctions.TryGetValue(instName, out var existing))
			return existing;

		TypeSymbol ResolveSubstitutedType(string typeName)
		{
			var substitutedTypeName = typeName;
			foreach (var kv in substitutionMap)
			{
				substitutedTypeName = SubstituteTypeToken(substitutedTypeName, kv.Key, kv.Value.Name);
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
		var instDecl = new FunctionDeclarationSyntax(templateDecl.Span, returnType.Name, instName, [], instParameters, instBody, modifier: templateDecl.Modifier);

		context.MonomorphizedFunctionDecls.Add(instDecl);

		if (context.SymbolUnits.TryGetValue(templateMangledName, out var templateUnit))
		{
			context.SymbolUnits[instName] = templateUnit;
		}

		var localScope = new SymbolTable(context.Globals);
		foreach (var p in parameters)
		{
			localScope.Declare(new VariableSymbol(p.Name, p.Type, p.Type is PointerTypeSymbol { IsMutable: true }) { IsInitialized = true });
		}

		CheckBlock(instBody, localScope, instDecl);

		return instSymbol;
	}

	// ---------------------------------------------------------------------------
	// Protocol-parameterized (structural / duck-typed implicit generic) dispatch.
	// A function with a protocol-typed parameter is lowered to a template and
	// monomorphized at each call site with the concrete argument type that
	// structurally conforms to the protocol's canonical member tokens. Conformance
	// is implicit — no `extension T : IProtocol` declaration exists for protocols.
	// ---------------------------------------------------------------------------

	private string? ResolveProtocolFunctionTemplateName(string name, SymbolTable scope)
	{
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.ProtocolFunctionTemplates.ContainsKey(localMangled)) return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = new List<string>(context.CurrentUnit.Usings.Select(u => u.NamespaceName));
			if (context.CurrentUnit.NamespaceDeclaration is not null)
				activeUsings.AddRange(context.CurrentUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.ProtocolFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.ProtocolFunctionTemplates.ContainsKey(name)) return name;
		return null;
	}

	/// <summary>
	/// Structural (duck-typed) protocol conformance: the concrete type satisfies
	/// every required protocol member. Phase-2 canonical token pre-matching builds
	/// tokens from the concrete type's resolved extension methods; a deferred
	/// TypeSymbol.Equals pass re-validates members whose Phase-1 tokens used raw
	/// (not yet resolvable) text, keeping structural matching topological and exact.
	/// </summary>
	private bool StructurallyConformsToProtocol(TypeSymbol type, ProtocolTypeSymbol proto)
	{
		var baseType = type is PointerTypeSymbol ptr ? ptr.ReferencedType : type;
		if (baseType is ProtocolTypeSymbol or InterfaceTypeSymbol) return false;

		// Width-lock invariant (Generic Contracts spec §6.A): a parameterized
		// protocol strictly matches only structures sharing the identical
		// type-parameter topology. A flat IntBag (Store(int)) can never satisfy
		// IContainer<T> even though its concrete tokens happen to match an
		// IContainer<int> instantiation.
		if (proto.GenericParameters.Count > 0 && GetConcreteGenericArity(baseType) != proto.GenericParameters.Count)
			return false;

		foreach (var member in proto.Members)
		{
			var matched = false;
			foreach (var candidate in GatherProtocolMemberCandidates(baseType, member.Name))
			{
				if (MemberStructurallyMatches(member, candidate, proto, baseType))
				{
					matched = true;
					break;
				}
			}

			// Default implementations (extension on the protocol, spec §4): a
			// conformer inherits the default unless it overrides the member.
			if (!matched && HasSatisfyingDefault(member, proto))
				matched = true;

			if (!matched) return false;
		}

		return true;
	}

	/// <summary>
	/// True when the protocol carries a default implementation (extension block on
	/// the protocol definition, spec §4) whose canonical token matches this member.
	/// Generic protocols are compared in width-lock placeholder form, so a default
	/// declared as `void Store(T item)` satisfies the member for every instantiation.
	/// </summary>
	private bool HasSatisfyingDefault(ProtocolMethodDeclarationSyntax member, ProtocolTypeSymbol proto)
	{
		var protoName = proto.Name;
		if (proto.GenericTypeArguments is not null)
		{
			var openBracket = protoName.IndexOf('<');
			if (openBracket > 0) protoName = protoName[..openBracket];
		}

		// The default for a member lives under the member's OWNING protocol
		// (a `:` base clause may aggregate members declared on a parent whose
		// own extension block carries the default).
		var ownerName = protoName;
		var ownerGenerics = proto.GenericParameters;
		if (context.ProtocolEffectiveMembers.TryGetValue(protoName, out var effective))
		{
			var owner = effective.FirstOrDefault(e => ReferenceEquals(e.Member, member));
			if (owner == default)
				owner = effective.FirstOrDefault(e => e.Member.Name == member.Name);
			if (owner != default)
			{
				ownerName = owner.OwnerProtocol;
				if (context.ProtocolTemplates.TryGetValue(owner.OwnerProtocol, out var ownerDecl))
					ownerGenerics = ownerDecl.GenericParameters;
			}
		}

		if (!context.ProtocolDefaults.TryGetValue(ownerName, out var defaults))
			return false;

		var memberToken = ProtocolCanonicalizer.BuildMemberToken(member, ownerGenerics, context, selfReplacement: null, proto.GenericTypeArguments);
		foreach (var (defaultName, decl) in defaults)
		{
			if (defaultName != member.Name) continue;
			var defaultToken = ProtocolCanonicalizer.BuildFunctionToken(decl, ownerGenerics, context, proto.GenericTypeArguments);
			if (defaultToken == memberToken) return true;
		}
		return false;
	}

	/// <summary>
	/// The generic type-parameter arity of a concrete type: 0 for flat types,
	/// the declared parameter count for generic templates/instances (both share
	/// the same topology). Used by the width-lock invariant.
	/// </summary>
	private int GetConcreteGenericArity(TypeSymbol baseType)
	{
		var name = baseType.Name;
		var openBracket = name.LastIndexOf('<');
		if (openBracket <= 0) return 0;

		var baseName = name[..openBracket];
		if (context.GenericStructTemplates.TryGetValue(baseName, out var structTemplate))
			return structTemplate.GenericParameters.Count;
		if (context.GenericUnionTemplates.TryGetValue(baseName, out var unionTemplate))
			return unionTemplate.GenericParameters.Count;
		return 0;
	}

	/// <summary>
	/// The "3-key walk": locate a concrete type's extension methods in every place
	/// an implementation could be declared — fully-qualified type name, leaf name
	/// (global-namespace extension blocks), and the call-site namespace/usings —
	/// mirroring how ordinary extension calls resolve.
	/// </summary>
	private List<FunctionSymbol> GatherProtocolMemberCandidates(TypeSymbol baseType, string memberName)
	{
		var results = new List<FunctionSymbol>();
		var leafName = baseType.Name.Contains('.')
			? baseType.Name[(baseType.Name.LastIndexOf('.') + 1)..]
			: baseType.Name;

		if (context.OverloadedFunctions.TryGetValue($"{baseType.Name}.{memberName}", out var qualified))
			results.AddRange(qualified);

		if (context.OverloadedFunctions.TryGetValue($"{leafName}.{memberName}", out var leaf))
			results.AddRange(leaf);

		if (context.CurrentUnit is not null)
		{
			var activeUsings = new List<string>(context.CurrentUnit.Usings.Select(u => u.NamespaceName));
			if (context.CurrentUnit.NamespaceDeclaration is not null)
				activeUsings.AddRange(context.CurrentUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

			foreach (var ns in activeUsings)
			{
				if (context.OverloadedFunctions.TryGetValue(context.GetMangledName($"{leafName}.{memberName}", ns), out var viaUsing))
					results.AddRange(viaUsing);
			}
		}

		return results;
	}

	private bool MemberStructurallyMatches(ProtocolMethodDeclarationSyntax member, FunctionSymbol candidate, ProtocolTypeSymbol proto, TypeSymbol baseType)
	{
		if (candidate.Parameters.Count == 0 || candidate.Parameters[0].Name != "this") return false;
		if (candidate.Parameters.Count - 1 != member.Parameters.Count) return false;

		// Phase-2 canonical pre-match: the token built from the concrete resolved
		// symbol must be a protocol member token (O(1) set membership).
		var concreteToken = BuildConcreteCanonicalToken(candidate, member.Name);
		if (proto.CanonicalMembers.Contains(concreteToken)) return true;

		// `Self`-anchored members never match by set membership (their stored
		// tokens keep the literal anchor): rebuild this member's canonical token
		// with Self substituted to the enclosing concrete type and compare exactly.
		if (MemberReferencesSelf(member)
			&& ProtocolCanonicalizer.BuildMemberToken(member, proto.GenericParameters, context, baseType.Name, proto.GenericTypeArguments) == concreteToken)
			return true;

		// Deferred semantic evaluation: the protocol member's types were not fully
		// qualified at Phase-1 (forward reference); compare resolved types exactly,
		// mapping `Self` to the concrete type.
		for (var i = 0; i < member.Parameters.Count; i++)
		{
			var protoParamType = ResolveProtocolMemberType(member.Parameters[i].Type, baseType, proto);
			if (protoParamType is null || !protoParamType.Equals(candidate.Parameters[i + 1].Type)) return false;
		}

		var protoReturnType = ResolveProtocolMemberType(member.ReturnType, baseType, proto);
		if (protoReturnType is not null && !protoReturnType.Equals(candidate.ReturnType)) return false;

		return true;
	}

	/// <summary>
	/// Resolves a protocol member type against the concrete type, mapping a raw
	/// `Self` (in any wrapper) to the concrete type and a generic protocol's type
	/// parameter to its concrete argument (deferred semantic re-validation of a
	/// generic instantiation whose tokens were not resolvable at Phase 1).
	/// </summary>
	private TypeSymbol? ResolveProtocolMemberType(string typeText, TypeSymbol baseType, ProtocolTypeSymbol proto)
	{
		var t = typeText.Trim();
		var prefix = "";
		if (t.StartsWith("refvar ", StringComparison.Ordinal)) { prefix = "refvar "; t = t[7..]; }
		else if (t.StartsWith("ref ", StringComparison.Ordinal)) { prefix = "ref "; t = t[4..]; }

		if (ProtocolCanonicalizer.StripWrappers(t) == "Self")
		{
			return prefix switch
			{
				"refvar " => new PointerTypeSymbol(baseType, isMutable: true),
				"ref " => new PointerTypeSymbol(baseType, isMutable: false),
				_ => baseType,
			};
		}

		if (proto.GenericTypeArguments is not null)
		{
			for (var i = 0; i < proto.GenericParameters.Count; i++)
			{
				if (t == proto.GenericParameters[i])
				{
					var argType = context.ResolveType(proto.GenericTypeArguments[i]);
					if (argType is null) return null;
					return prefix switch
					{
						"refvar " => new PointerTypeSymbol(argType, isMutable: true),
						"ref " => new PointerTypeSymbol(argType, isMutable: false),
						_ => argType,
					};
				}
			}
		}

		return context.ResolveType(typeText);
	}

	private static bool MemberReferencesSelf(ProtocolMethodDeclarationSyntax member)
	{
		return ReferencesSelf(member.ReturnType) || member.Parameters.Any(p => ReferencesSelf(p.Type));
	}

	private static bool ReferencesSelf(string typeText)
	{
		return ProtocolCanonicalizer.StripWrappers(typeText) == "Self";
	}

	private string BuildConcreteCanonicalToken(FunctionSymbol candidate, string memberName)
	{
		var paramTokens = new List<string>();
		for (var i = 1; i < candidate.Parameters.Count; i++)
			paramTokens.Add(candidate.Parameters[i].Type.Name);

		return $"{candidate.ReturnType.Name}:{memberName}({string.Join(",", paramTokens)})";
	}

	/// <summary>
	/// Resolves a call to a protocol-parameterized function by monomorphizing the
	/// template with the concrete structurally conforming argument types. Reports
	/// the specific id-less diagnostic (conformance / argument-count / conflicting
	/// concrete) and returns null so the caller suppresses the generic "no
	/// overload" message.
	/// </summary>
	private FunctionSymbol? TryResolveProtocolCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope)
	{
		var templateName = ResolveProtocolFunctionTemplateName(call.FunctionName, scope);
		if (templateName is null) return null;

		var templateDecl = context.ProtocolFunctionTemplates[templateName];
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		if (argTypes.Count != templateDecl.Parameters.Count)
		{
			context.Diagnostics.Report(currentFileContext, call.Span,
				$"Function '{call.FunctionName}' expects {templateDecl.Parameters.Count} argument(s) but received {argTypes.Count}");
			return null;
		}

		// Conforming concrete types whose protocol defaults must be materialized
		// before the body is validated (so inherited-member calls resolve).
		var conformedPairs = new List<(TypeSymbol Concrete, ProtocolTypeSymbol Proto)>();

		// Build the substitution map: each protocol-typed parameter maps to its concrete arg type.
		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < templateDecl.Parameters.Count; i++)
		{
			var param = templateDecl.Parameters[i];

			// Unwrap an optional ref/refvar prefix to discover the underlying protocol name.
			var isRefParam = param.Type.StartsWith("refvar ", StringComparison.Ordinal)
				|| param.Type.StartsWith("ref ", StringComparison.Ordinal);
			var protocolTypeName = isRefParam
				? (param.Type.StartsWith("refvar ", StringComparison.Ordinal) ? param.Type[7..] : param.Type[4..])
				: param.Type;

			if (context.ResolveType(protocolTypeName) is not ProtocolTypeSymbol proto) continue;

			// For a ref/refvar protocol parameter, the concrete argument arrives as a pointer;
			// substitute the referent type name so ref/refvar is supplied by the template's
			// own parameter string (e.g. "refvar IShape" -> "refvar Rect").
			var concreteArg = argTypes[i];
			var concrete = concreteArg is PointerTypeSymbol cPtr ? cPtr.ReferencedType : concreteArg;

			if (concrete is ProtocolTypeSymbol or InterfaceTypeSymbol)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Protocol parameter '{param.Name}' of function '{call.FunctionName}' cannot be resolved to a concrete conforming type; argument is abstract protocol type '{concrete.Name}'");
				return null;
			}

			if (!StructurallyConformsToProtocol(concrete, proto))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not structurally conform to protocol '{proto.Name}' for parameter '{param.Name}'");
				return null;
			}

			// Protocol `for ...` requires-clause (lazy, at the dispatch call site):
			// the concrete type must itself satisfy the named contract. Missing or
			// unresolvable constraints are treated conservatively as non-conforming.
			if (proto.Constraint is not null && !SatisfiesProtocolConstraint(concrete, proto.Constraint))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not satisfy the requires-clause '{proto.Constraint}' of protocol '{proto.Name}'.");
				return null;
			}

			conformedPairs.Add((concrete, proto));

			// Ambiguity rule (spec §7.C): a concrete type matching several contracts
			// (declared in different extension namespaces) for the same member
			// signature yields more than one distinct implementation -> error.
			if (ReportProtocolAmbiguity(concrete, proto, call))
				return null;

			if (substitutionMap.TryGetValue(protocolTypeName, out var existing) && existing.Name != concrete.Name)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Protocol parameter '{param.Name}' requires a single concrete type, but both '{existing.Name}' and '{concrete.Name}' were passed");
				return null;
			}

			substitutionMap[protocolTypeName] = concrete;
		}

		if (substitutionMap.Count == 0) return null;

		// Inherited default implementations (spec §4): materialize a substituted
		// copy of each default onto its conforming concrete type so calls inside
		// the monomorphized body resolve to a real function.
		foreach (var (conformed, conformedProto) in conformedPairs.Distinct())
			MaterializeProtocolDefaults(conformed, conformedProto);

		return InstantiateProtocolFunction(templateDecl, substitutionMap, scope);
	}

	/// <summary>
	/// Lazy protocol `for ...` requires-clause check (spec §7.B). The concrete
	/// type must satisfy the named contract: a nominal interface requires
	/// (transitive) conformance membership; a structural protocol requires
	/// structural conformance. Unknown contracts are conservatively treated as
	/// non-satisfying (the constraint names a contract this module lacks).
	/// </summary>
	private bool SatisfiesProtocolConstraint(TypeSymbol concrete, string constraintText)
	{
		var contract = ResolveContractBase(constraintText, concrete.Name);
		switch (contract)
		{
			case ProtocolTypeSymbol consProto:
				return StructurallyConformsToProtocol(concrete, consProto);
			case InterfaceTypeSymbol consIface:
				var baseType = concrete is PointerTypeSymbol ptr ? ptr.ReferencedType : concrete;
				return context.Conformance.TryGetValue(baseType.Name, out var ifaces) && ifaces.Contains(consIface.Name);
			default:
				return false;
		}
	}

	/// <summary>
	/// Resolves a requires-clause type in the concrete type's context: literal
	/// `Self` is replaced with the concrete type name and a generic instantiation
	/// (e.g. `IComparable&lt;Self&gt;`) is stripped to its base contract name
	/// (generic interfaces are not instantiable in this model).
	/// </summary>
	private TypeSymbol? ResolveContractBase(string constraintText, string concreteName)
	{
		var substituted = constraintText.Replace("Self", concreteName);
		var openBracket = substituted.IndexOf('<');
		var baseName = (openBracket > 0 ? substituted[..openBracket] : substituted).Trim();
		return context.ResolveType(baseName);
	}

	/// <summary>
	/// Reports an ambiguity (spec §7.C) when a concrete type matches more than one
	/// distinct implementation for any member of the protocol (e.g. extension
	/// blocks declared in different namespaces both satisfying the same signature).
	/// Members satisfied by a default implementation do not compete.
	/// </summary>
	private bool ReportProtocolAmbiguity(TypeSymbol concrete, ProtocolTypeSymbol proto, CallExpressionSyntax call)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		foreach (var member in proto.Members)
		{
			var matches = GatherProtocolMemberCandidates(concrete, member.Name)
				.Where(candidate => MemberStructurallyMatches(member, candidate, proto, concrete))
				.Distinct()
				.Count();
			if (matches > 1)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Ambiguous implementation of '{member.Name}' for protocol '{proto.Name}' on type '{concrete.Name}': multiple extension methods match the required signature.");
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Materialize a protocol's default implementations (extension blocks on the
	/// protocol definition) as real methods on a conforming concrete type.
	/// The default's `this` receiver becomes a pointer to the concrete type, the
	/// declaration is registered under "{Concrete}.{Method}", and the raw body is
	/// queued for codegen via the monomorphized-function pipeline. A conformer
	/// that declares its own matching method overrides the default (identical
	/// overloaded names collide, own registration wins by order).
	/// </summary>
	private void MaterializeProtocolDefaults(TypeSymbol concrete, ProtocolTypeSymbol proto)
	{
		var baseType = concrete is PointerTypeSymbol ptr ? ptr.ReferencedType : concrete;
		if (baseType is not (StructTypeSymbol or UnionTypeSymbol)) return;

		var protoName = proto.Name;
		if (proto.GenericTypeArguments is not null)
		{
			var openBracket = protoName.IndexOf('<');
			if (openBracket > 0) protoName = protoName[..openBracket];
		}

		var materializationKey = $"{baseType.Name}|{protoName}";
		if (!context.MaterializedProtocolDefaults.Add(materializationKey)) return;

		// Collect the inherited defaults for this conformer: walk the effective
		// member list and pick, for each member it does not implement itself, the
		// satisfying default from that member's OWNING protocol's registry.
		IEnumerable<(string MemberName, FunctionDeclarationSyntax Decl)> inheritedDefaults;
		if (context.ProtocolEffectiveMembers.TryGetValue(protoName, out var effective))
		{
			var list = new List<(string MemberName, FunctionDeclarationSyntax Decl)>();
			foreach (var (owner, member) in effective)
			{
				var ownerGenerics = context.ProtocolTemplates.TryGetValue(owner, out var ownerDecl) ? ownerDecl.GenericParameters : proto.GenericParameters;
				if (!context.ProtocolDefaults.TryGetValue(owner, out var ownerDefaults)) continue;

				var memberToken = ProtocolCanonicalizer.BuildMemberToken(member, ownerGenerics, context, selfReplacement: null, proto.GenericTypeArguments);
				foreach (var (dn, ddecl) in ownerDefaults)
				{
					if (dn != member.Name) continue;
					var defaultToken = ProtocolCanonicalizer.BuildFunctionToken(ddecl, ownerGenerics, context, proto.GenericTypeArguments);
					if (defaultToken != memberToken) continue;
					list.Add((dn, ddecl));
					break;
				}
			}
			inheritedDefaults = list;
		}
		else
		{
			if (!context.ProtocolDefaults.TryGetValue(protoName, out var flatDefaults)) return;
			inheritedDefaults = flatDefaults;
		}

		foreach (var (memberName, decl) in inheritedDefaults)
		{
			var baseMangledName = $"{baseType.Name}.{memberName}";

			var thisParamType = new PointerTypeSymbol(baseType, isMutable: false);
			var parameters = new List<ParameterSymbol> { new ParameterSymbol("this", thisParamType) };
			var hasBadParam = false;
			foreach (var p in decl.Parameters)
			{
				var paramType = context.ResolveType(p.Type);
				if (paramType is null) { hasBadParam = true; break; }
				parameters.Add(new ParameterSymbol(p.Name, paramType));
			}
			if (hasBadParam) continue;

			var returnType = context.ResolveType(decl.ReturnType);
			if (returnType is null) continue;

			var overloadedName = context.GetOverloadedMangledName(baseMangledName, parameters.Select(q => q.Type).ToList());
			if (context.Globals.Lookup(overloadedName) is not null) continue;

			var newSymbol = new FunctionSymbol(overloadedName, returnType, parameters);
			context.Globals.Declare(newSymbol);

			if (!context.OverloadedFunctions.TryGetValue(baseMangledName, out var candidates))
			{
				candidates = [];
				context.OverloadedFunctions[baseMangledName] = candidates;
			}
			candidates.Add(newSymbol);

			context.SymbolUnits[overloadedName] = context.CurrentUnit!;

			var instDecl = new FunctionDeclarationSyntax(decl.Span, decl.ReturnType, overloadedName, [], decl.Parameters, decl.Body);
			context.MonomorphizedFunctionDecls.Add(instDecl);
		}
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
						newType = SubstituteTypeToken(newType, kv.Key, kv.Value.Name);
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
						newOp = SubstituteTypeToken(newOp, kv.Key, kv.Value.Name);
					}
				}

				return new UnaryExpressionSyntax(unary.Span, newOp, SubstituteExpressionGenerics(unary.Operand, substitutionMap));

			case CallExpressionSyntax call:
				var newTypeArgs = call.TypeArguments.Select(t => {
					var substituted = t;
					foreach (var kv in substitutionMap)
					{
						substituted = SubstituteTypeToken(substituted, kv.Key, kv.Value.Name);
					}

					return substituted;
				}).ToList();
				var newArgs = call.Arguments.Select(a => SubstituteExpressionGenerics(a, substitutionMap)).ToList();
				return new CallExpressionSyntax(call.Span, call.FunctionName, newTypeArgs, newArgs);

			case StructInitializationExpressionSyntax structInit:
				var newTypeName = structInit.StructTypeName;
				foreach (var kv in substitutionMap)
				{
					newTypeName = SubstituteTypeToken(newTypeName, kv.Key, kv.Value.Name);
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

	private FunctionSymbol? ResolveOverloadedFunction(string name, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope, CallExpressionSyntax? call = null)
	{
		var candidates = new List<FunctionSymbol>();
		var baseName = name;
		var adjustedArgTypes = new List<TypeSymbol>(argTypes);

		var isDottedExtension = false;

		// Detect dotted extension method call
		if (name.Contains('.'))
		{
			var parts = name.Split('.');
			var receiverName = parts[0];
			var methodName = parts[1];

			if (scope.Lookup(receiverName) is VariableSymbol receiverSymbol)
			{
				var receiverType = receiverSymbol.Type;
				if (receiverType is PointerTypeSymbol ptr)
					receiverType = ptr.ReferencedType;

				if (receiverType is StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol)
				{
					isDottedExtension = true;
					// Resolve using "Option<int>.IsSome" as the base name
					baseName = $"{receiverType.Name}.{methodName}";

					// Prepend the receiver's reference type as the first argument!
					var receiverRefType = new PointerTypeSymbol(receiverType, isMutable: receiverSymbol.IsMutable);
					adjustedArgTypes.Insert(0, receiverRefType);
				}
			}
		}

		if (!isDottedExtension)
		{
			var constructorName = name;
			if (call != null && call.TypeArguments.Count > 0 && !name.Contains('<'))
			{
				var concreteTypeName = $"{name}<{string.Join(", ", call.TypeArguments)}>";
				var resolvedType = context.ResolveType(concreteTypeName);
				if (resolvedType != null)
				{
					constructorName = resolvedType.Name;
				}
			}

			// Dual-Name Lookup: Support both fully-qualified namespace name and short name
			var shortConstructorName = constructorName;
			if (shortConstructorName.Contains('.'))
			{
				shortConstructorName = shortConstructorName.Substring(shortConstructorName.LastIndexOf('.') + 1);
			}

			if (context.Constructors.ContainsKey(constructorName) || context.Constructors.ContainsKey(shortConstructorName))
			{
				baseName = context.Constructors.ContainsKey(constructorName) ? constructorName : shortConstructorName;
				var ctorType = context.ResolveType(baseName);

				if (ctorType is not null)
					adjustedArgTypes.Insert(0, new PointerTypeSymbol(ctorType, isMutable: true));
			}
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
			else if (arg.Equals(TypeSymbol.Null) && (param is RawPointerTypeSymbol || (param is UnionTypeSymbol union && union.IsOption)))
			{
				score += 3; // Null matches raw pointers and Option types!
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
			else if (TypeSymbol.IsNumericIntegerType(param) && TypeSymbol.IsNumericIntegerType(arg) && !param.Equals(arg))
			{
				score += 1; // Implicit integer width conversion (byte->int, int->long, etc.)
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
			return new ArrayTypeSymbol(valueType, checked((int)countLit.Value));
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
		var extendedType = context.ResolveType(extendedTypeName);
		if (extendedType is ProtocolTypeSymbol)
		{
			// PROTOCOL DEFAULT BODIES: validated once at declaration against a
			// this-free scope (no receiver object or flat struct fields exist).
			// A default body may only reference globals/functions and its own
			// explicit parameters.
			var baseUnsafeDepth = _unsafeDepth;
			_unsafeDepth = IsUnsafeFunction(method) ? 1 : 0;
			var protoScope = new SymbolTable(context.Globals);
			foreach (var p in method.Parameters)
			{
				var pt = context.ResolveType(p.Type);
				if (pt is not null)
					protoScope.Declare(new VariableSymbol(p.Name, pt, isMutable: false) { IsInitialized = true });
			}
			CheckBlock(method.Body, protoScope, method);
			_unsafeDepth = baseUnsafeDepth;
			return;
		}

		if (extendedType is not (StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol)) return;

		var baseUnsafeDepth2 = _unsafeDepth;
		_unsafeDepth = IsUnsafeFunction(method) ? 1 : 0;

		// 1. Static AST Mutation Scan: Infer if the method modifies any fields
		var isMutating = forceMutableThis || (extendedType is StructTypeSymbol structType && DetectFieldMutation(method.Body, structType));

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

		// 3. Populate local scope with fields/variants so they can be written as flat local variables
		var localScope = new SymbolTable(context.Globals);
		if (extendedType is StructTypeSymbol st)
		{
			foreach (var field in st.Fields)
			{
				localScope.Declare(new VariableSymbol(field.Name, field.Type, isMutable: true) { IsInitialized = true });
			}
		}
		else if (extendedType is UnionTypeSymbol ut)
		{
			foreach (var field in ut.Fields)
			{
				localScope.Declare(new VariableSymbol(field.Name, field.Type, isMutable: true) { IsInitialized = true });
			}
		}
		else if (extendedType is EnumTypeSymbol enumExtType)
		{
			// Enums are flat scalars: 'this' reads as the enum value itself and the
			// variant names (Active, Pending, ...) are visible without qualification.
			localScope.Declare(new VariableSymbol("this", enumExtType, isMutable: false) { IsInitialized = true });
			foreach (var variant in enumExtType.Variants)
			{
				localScope.Declare(new VariableSymbol(variant.Name, enumExtType, isMutable: false) { IsInitialized = true });
			}
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
		_unsafeDepth = baseUnsafeDepth2;
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

	private void CheckSwitchStatement(SwitchStatementSyntax sw, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		CheckExpression(sw.Expression, scope);
		var exprType = GetExpressionType(sw.Expression, scope);
		if (exprType is null) return;

		if (exprType is PointerTypeSymbol ptr)
		{
			exprType = ptr.ReferencedType;
		}

		if (exprType is EnumTypeSymbol enumType)
		{
			CheckEnumSwitch(sw, enumType, scope, currentFunc);
			return;
		}

		if (exprType is not UnionTypeSymbol unionType)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, sw.Expression.Span, "Switch statement target must be a union type.");
			return;
		}

		var matchedVariants = new HashSet<string>();
		var hasDefault = false;

		foreach (var c in sw.Cases)
		{
			if (c.IsDefault || c.VariantName == "_")
			{
				hasDefault = true;
				CheckBlock(new BlockStatementSyntax(c.Span, c.Body), new SymbolTable(scope), currentFunc);
				continue;
			}

			matchedVariants.Add(c.VariantName);
			var variant = unionType.FindField(c.VariantName);
			if (variant is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, c.Span, $"Union '{unionType.Name}' does not contain variant '{c.VariantName}'");
				continue;
			}

			var caseScope = new SymbolTable(scope);

			if (c.VariableName is not null)
			{
				if (variant.IsVoidVariant)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, c.Span, $"Void variant '{c.VariantName}' cannot carry a promoted variable.");
					continue;
				}

				// Type Promotion (Reference targets promote to pointers; value targets copy/move)
				TypeSymbol promotedType;
				if (unionType.IsNpoEligible)
				{
					// NPO: the payload IS the reference already (flat pointer). Extracting it under
					// a ref/refvar switch yields the inner reference directly, lock-protected.
					if (variant.Type is not PointerTypeSymbol)
					{
						promotedType = variant.Type;
					}
					else if (GetExpressionType(sw.Expression, scope) is not PointerTypeSymbol)
					{
						// A by-value switch over an NPO reference option would copy the ref out of the
						// borrow lock, creating an unsound aliased reference.
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, c.Span,
							$"Cannot pattern-match '{c.VariantName} {c.VariableName}' by value on a nullable reference option; switch on 'ref'/'refvar' to extract the reference safely.");
						continue;
					}
					else
					{
						promotedType = variant.Type;
					}
				}
				else if (GetExpressionType(sw.Expression, scope) is PointerTypeSymbol targetPtr)
				{
					promotedType = new PointerTypeSymbol(variant.Type, isMutable: targetPtr.IsMutable);
				}
				else
				{
					promotedType = variant.Type;
				}

				caseScope.Declare(new VariableSymbol(c.VariableName, promotedType, isMutable: false) { IsInitialized = true });
			}

			CheckBlock(new BlockStatementSyntax(c.Span, c.Body), caseScope, currentFunc);
		}

		// Exhaustive Switch-Matching check
		if (!hasDefault)
		{
			foreach (var variant in unionType.Fields)
			{
				if (!matchedVariants.Contains(variant.Name))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, sw.Span, $"Switch statement is not exhaustive. Missing case for variant '{variant.Name}'.");
				}
			}
		}
	}

	private void CheckEnumSwitch(SwitchStatementSyntax sw, EnumTypeSymbol enumType, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		var matchedVariants = new HashSet<string>();
		var hasDefault = false;

		foreach (var c in sw.Cases)
		{
			if (c.IsDefault || c.VariantName == "_")
			{
				hasDefault = true;
				CheckBlock(new BlockStatementSyntax(c.Span, c.Body), new SymbolTable(scope), currentFunc);
				continue;
			}

			if (c.VariableName is not null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, c.Span, "Enum variants cannot carry a promoted variable.");
				continue;
			}

			matchedVariants.Add(c.VariantName);
			var variant = enumType.FindVariant(c.VariantName);
			if (variant is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, c.Span, $"Enum '{enumType.Name}' does not contain variant '{c.VariantName}'");
				continue;
			}

			CheckBlock(new BlockStatementSyntax(c.Span, c.Body), new SymbolTable(scope), currentFunc);
		}

		if (!hasDefault)
		{
			foreach (var variant in enumType.Variants)
			{
				if (!matchedVariants.Contains(variant.Name))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, sw.Span, $"Switch statement is not exhaustive. Missing case for variant '{variant.Name}'.");
				}
			}
		}
	}

	private void CheckLargeUnionByValueArgument(ExpressionSyntax arg, TypeSymbol? paramType, SymbolTable scope)
	{
		if (paramType is PointerTypeSymbol)
			return;

		var type = GetExpressionType(arg, scope);
		if (type is not UnionTypeSymbol unionType)
			return;

		var size = Classification.CalculateByteSize(unionType);
		if (size > 16)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, arg.Span,
				$"Union '{unionType.Name}' is {size} bytes. Passing by value is forbidden for unions larger than 16 bytes; pass by 'ref'/'refvar' instead.");
		}
	}

	private void CheckEnumIntMismatch(TypeSymbol? left, TypeSymbol? right, TextSpan span)
	{
		if (left is null || right is null) return;

		var leftIsEnum = left is EnumTypeSymbol;
		var rightIsEnum = right is EnumTypeSymbol;
		if (leftIsEnum == rightIsEnum) return;

		var enumType = leftIsEnum ? left : right;
		var otherType = leftIsEnum ? right : left;
		if (!TypeSymbol.IsIntegerType(otherType)) return;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, span,
			$"Implicit conversion between enum '{enumType.Name}' and '{otherType.Name}' is forbidden; use an explicit cast.");
	}

	private void CheckUnaryCast(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		if (!unary.Operator.StartsWith("(") || !unary.Operator.EndsWith("*)") || unary.Operator.Length < 4)
			return;

		var operandType = GetExpressionType(unary.Operand, scope);
		if (operandType is not UnionTypeSymbol optionUnion || !optionUnion.IsNpoEligible)
			return;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, unary.Span,
			$"Cannot cast nullable reference option '{optionUnion.Name}' directly to a raw pointer; pattern-match it (switch on 'ref'/'refvar') to extract a non-null reference first.");
	}

	private TypeSymbol? GetUnaryExpressionType(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		if (unary.Operator == "&")
		{
			var opType = GetExpressionType(unary.Operand, scope);
			return opType is not null ? new RawPointerTypeSymbol(opType) : null;
		}

		if (unary.Operator == "*")
		{
			var opType = GetExpressionType(unary.Operand, scope);
			if (opType is RawPointerTypeSymbol rawPtr)
				return rawPtr.ElementType;
			if (opType is PointerTypeSymbol ptr)
				return ptr.ReferencedType;
return null;
		}

		if (unary.Operator.Length >= 3 && unary.Operator.StartsWith("(") && unary.Operator.EndsWith(")"))
		{
			var result = context.ResolveType(unary.Operator[1..^1]);
			if (result is EnumTypeSymbol castEnum && _unsafeDepth == 0)
			{
				// Safe/unbound zone: an explicit (Enum)integer cast is a checked
				// conversion yielding Option<Enum> (None when the value matches no
				// declared variant); the raw enum is only available in unsafe code.
				var operandType = GetExpressionType(unary.Operand, scope);
				if (operandType is not EnumTypeSymbol && TypeSymbol.IsIntegerType(operandType))
					return context.ResolveType($"Option<{castEnum.Name}>") ?? result;
			}
			return result;
		}

		return GetExpressionType(unary.Operand, scope);
	}
}
