using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;
using Cvolo.Analysis.VisibilityChecks;
using Cvolo.Analysis.Passes.Validation;
using Cvolo.Analysis.Resolution;
using Cvolo.Analysis.Contracts;

namespace Cvolo.Analysis.Passes;

public sealed class ValidationPass(BindingContext context)
{
	private ClassificationAnalyzer? _classification;
	private ClassificationAnalyzer Classification => _classification ??= new ClassificationAnalyzer(context);

	private OverloadResolver? _overloadResolver;
	/// <summary>Shared overload candidate discovery and signature scoring service.</summary>
	private OverloadResolver Overloads => _overloadResolver ??= new OverloadResolver(context);
	private CallResolver? _callResolver;
	/// <summary>Shared callable-target resolver for ordinary, synthetic enum, and delegate-value calls.</summary>
	private CallResolver Calls => _callResolver ??= new CallResolver(context, Overloads);

	private InterfaceConformance? _interfaceConformance;
	/// <summary>Shared nominal interface-conformance service.</summary>
	private InterfaceConformance Interfaces => _interfaceConformance ??= new InterfaceConformance(context);
	private ProtocolConformance? _protocolConformance;
	/// <summary>Shared structural protocol-conformance and ambiguity service.</summary>
	private ProtocolConformance Protocols => _protocolConformance ??= new ProtocolConformance(context, Interfaces);
	private ProtocolDefaultMaterializer? _protocolDefaults;
	/// <summary>Shared materializer for inherited protocol default implementations.</summary>
	private ProtocolDefaultMaterializer ProtocolDefaults => _protocolDefaults ??= new ProtocolDefaultMaterializer(context);

	/// <summary>
	/// Mutable traversal state for the validation run. The state object is kept separate from the
	/// semantic services owned by <see cref="BindingContext"/> so later validators can share the same
	/// single-pass traversal state without depending on <see cref="ValidationPass"/> itself.
	/// </summary>
	private readonly ValidationContext _validation = new();

	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		_validation.Units = units as IReadOnlyList<CompilationUnitSyntax> ?? units.ToList();
		foreach (var unit in _validation.Units)
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

					if (isInterfaceTemplate)
						continue;

					// Protocol-parameterized functions are likewise implicit templates: a protocol name has
					// no value representation, so the body is validated when monomorphized at a call site.
					var isProtocolTemplate = context.ProtocolFunctionTemplates.ContainsKey(ifaceTemplateName);

					if (isProtocolTemplate)
						continue;

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
				else if (member is ExposeExternBlockSyntax exportBlock)
				{
					foreach (var exportFunc in exportBlock.Functions)
					{
						var isTemplate = exportFunc.GenericParameters.Count > 0 && exportFunc.GenericParameters.Any(p => context.ResolveType(p) == null);
						var ifaceTemplateName = context.GetMangledName(exportFunc.Name, context.CurrentNamespace);
						var isInterfaceTemplate = context.InterfaceFunctionTemplates.ContainsKey(ifaceTemplateName);
						var isProtocolTemplate = context.ProtocolFunctionTemplates.ContainsKey(ifaceTemplateName);
						if (isTemplate || isInterfaceTemplate || isProtocolTemplate)
							continue;

						// Exposed functions are never generic; fall back to direct body validation.
						CheckFunctionBody(exportFunc);
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
			var pending = context.MonomorphizedExtensionDecls.Where(d =>
			{
				var name = context.MonomorphizedExtensionNames[d];
				return !validatedMonomorphized.Contains(name);
			}).ToList();

			if (pending.Count == 0)
				break;

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
		if (extendedType is null)
			return;

		var wrapper = ctor.ToFunctionDeclaration();

		// Validate the body like any extension method, but "this" is always mutable:
		// a constructor's purpose is to populate fields.
		CheckExtensionMethodBody(extendedTypeName, wrapper, forceMutableThis: true);

		// Constructor chaining via ': this(...)': the delegating constructor forwards
		// to a sibling constructor of the same type, which is responsible for
		// initialising all fields. The delegating body should therefore be empty.
		if (ctor.HasConstructorInitializer)
		{
			CheckConstructorInitializer(extendedType, ctor);
			return;
		}

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
					ctor.NameSpan,
					$"Defensive initialization: constructor '{extendedTypeName}' does not initialize field '{field.Name}'."
				);
			}
		}
	}

	private void CheckConstructorInitializer(StructTypeSymbol extendedType, ConstructorDeclarationSyntax ctor)
	{
		// 1. Build a scope mirroring the delegating constructor's own parameters plus
		//    the mutable 'this' destination storage, then type-check the initializer args.
		var scope = new SymbolTable(context.Globals);
		scope.Declare(new VariableSymbol("this", new PointerTypeSymbol(extendedType, isMutable: true), isMutable: true)
		{
			IsInitialized = true,
			Origin = OriginKind.Parameter
		});

		var ctorParamTypes = new List<TypeSymbol>();
		foreach (var p in ctor.Parameters)
		{
			var pt = context.ResolveType(p.Type);
			if (pt is null)
				continue;

			ctorParamTypes.Add(pt);
			scope.Declare(new VariableSymbol(p.Name, pt, isMutable: false)
			{
				IsInitialized = true,
				Origin = OriginKind.Parameter
			});
		}

		var argTypes = new List<TypeSymbol>();
		foreach (var arg in ctor.ConstructorArguments!)
		{
			CheckExpression(arg, scope);
			argTypes.Add(GetExpressionType(arg, scope) ?? TypeSymbol.Int);
		}

		// 2. Overload resolution: pick the sibling constructor (same type, by
		//    construction) whose signature best matches the initializer arguments.
		var target = ResolveInitializerTarget(extendedType, argTypes);

		// 3. The delegating constructor's own registerable name (for the code generator).
		var callerParams = new List<TypeSymbol> { new PointerTypeSymbol(extendedType, isMutable: true) };
		callerParams.AddRange(ctorParamTypes);
		var callerName = context.GetOverloadedMangledName(extendedType.Name, callerParams);

		if (target is not null)
		{
			context.ConstructorDelegationTargets[callerName] = target;

			// No matching constructor for the initializer arguments.
			// 4. Accessibility: the target must be visible from the delegating constructor.
			if (target.Visibility == Visibility.Private
				&& target.DeclaringUnit is not null
				&& target.DeclaringUnit != context.CurrentUnit)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					ctor.ConstructorInitializerSpan ?? ctor.NameSpan,
					$"Constructor '{ctor.StructName}' is not accessible from the current constructor.");
			}

			// 5. Cycle detection: follow the delegation chain; if it loops back onto
			//    this constructor's chain, report cyclic delegation.
			if (CheckConstructorCycle(extendedType, ctor, target, callerName))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					ctor.ConstructorInitializerSpan ?? ctor.NameSpan,
					"Constructor initializer `this(...)` cannot call itself (cyclic delegation).",
					DiagnosticIds.CyclicConstructorDelegation);
				return;
			}
		}
		else
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			var sigString = string.Join(", ", argTypes.Select(t => t.Name));
			context.Diagnostics.Report(
				currentFileContext,
				ctor.ConstructorInitializerSpan ?? ctor.NameSpan,
				$"No constructor of '{extendedType.Name}' matches initializer argument types ({sigString}).");
			return;
		}

		// 6. Empty-body warning: a delegating constructor's body should defer all logic.
		if (ctor.Body.Statements.Count > 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.ReportWarning(
				currentFileContext,
				ctor.Body.Span,
				"Constructor body is not empty after `this(...)`; consider moving logic to the target constructor.",
				DiagnosticIds.NonEmptyDelegatingConstructorBody);
		}
	}

	/// <summary>Follows the delegation chain from <paramref name="target"/> and reports
	/// true when it loops back onto the caller's chain (direct or indirect recursion).</summary>
	private bool CheckConstructorCycle(StructTypeSymbol extendedType, ConstructorDeclarationSyntax caller, FunctionSymbol target, string callerChainKey)
	{
		var visiting = new HashSet<string> { callerChainKey };
		var current = target;
		var steps = 0;

		while (current is not null && steps < 1024)
		{
			if (!visiting.Add(current.Name))
				return true;

			var targetDecl = FindConstructorDeclaration(current);
			if (targetDecl is null || !targetDecl.HasConstructorInitializer)
				return false;

			var nextArgs = new List<TypeSymbol>();
			foreach (var arg in targetDecl.ConstructorArguments!)
				nextArgs.Add(GetExpressionType(arg, new SymbolTable(context.Globals)) ?? TypeSymbol.Int);

			current = ResolveInitializerTarget(extendedType, nextArgs);
			steps++;
		}

		return false;
	}

	private ConstructorDeclarationSyntax? FindConstructorDeclaration(FunctionSymbol symbol)
	{
		// Map a registered constructor symbol back to its declaration by scanning the
		// declared/monomorphized extension blocks for a matching signature.
		var paramSig = symbol.Parameters.Skip(1).Select(p => p.Type.Name).ToList();
		var extendedTypeName = symbol.Parameters[0].Type.Name;

		foreach (var unit in _validation.Units)
		{
			var members = unit.NamespaceDeclaration != null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var decl in members)
			{
				if (decl is not ExtensionDeclarationSyntax ext || ext.ExtendedTypeName != extendedTypeName)
					continue;

				foreach (var ctor in ext.Constructors)
				{
					if (SignaturesMatch(ctor, paramSig))
						return ctor;
				}
			}
		}

		foreach (var mono in context.MonomorphizedExtensionDecls)
		{
			if (mono is not ConstructorDeclarationSyntax monoCtor || monoCtor.StructName != extendedTypeName)
				continue;

			if (monoCtor.Parameters.Count == paramSig.Count)
				return monoCtor;
		}

		return null;
	}

	private bool SignaturesMatch(ConstructorDeclarationSyntax ctor, List<string> paramSig)
	{
		if (ctor.Parameters.Count != paramSig.Count)
			return false;

		for (var i = 0; i < paramSig.Count; i++)
		{
			if (context.ResolveType(ctor.Parameters[i].Type)?.Name != paramSig[i])
				return false;
		}

		return true;
	}

	private FunctionSymbol? ResolveInitializerTarget(StructTypeSymbol extendedType, IReadOnlyList<TypeSymbol> argTypes)
	{
		FunctionSymbol? target = null;
		if (context.Constructors.TryGetValue(extendedType.Name, out var registeredCtors))
		{
			var bestScore = -1;
			foreach (var candidate in registeredCtors)
			{
				if (candidate is null)
					continue;

				// The candidate's own parameter list (excluding its implicit 'this').
				var candParams = candidate.Parameters.Skip(1).Select(p => p.Type).ToList();
				if (candParams.Count != argTypes.Count)
					continue;

				var score = Overloads.CompareSignatureExactly(candParams, argTypes);
				if (score > bestScore)
				{
					bestScore = score;
					target = candidate;
				}
			}
		}

		return target;
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
		// 0. Check for generic parameter visibility leaks (CVL1038) on public functions
		if (func.Visibility == Visibility.Public)
		{
			var retType = context.ResolveType(func.ReturnType);
			CheckGenericVisibilityLeak(func.NameSpan, retType, func.Name);

			foreach (var param in func.Parameters)
			{
				var paramType = context.ResolveType(param.Type);
				CheckGenericVisibilityLeak(param.Span, paramType, func.Name);
			}
		}

		// 1. Guard for bodyless functions: must have [Intrinsic]
		if (!func.HasBody)
		{
			if (context.CurrentUnit is not null && context.ExternalPackageUnits.Contains(context.CurrentUnit))
				return;

			var hasIntrinsic = func.Attributes.Any(a => a.Name is "Intrinsic" or "System.Intrinsic" or "IntrinsicAttribute");
			if (!hasIntrinsic)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, func.NameSpan, $"Function '{func.Name}' must declare a body unless decorated with '[Intrinsic]'.");
			}

			return;
		}

		var baseUnsafeDepth = _validation.UnsafeDepth;
		var baseInUnbound = _validation.InUnbound;
		_validation.UnsafeDepth = IsUnsafeFunction(func) ? 1 : 0;
		_validation.InUnbound = func.Modifier == SafetyTier.Unbound;
		var baseEnclosingFunction = _validation.EnclosingFunction;
		_validation.EnclosingFunction = func;
		var localScope = new SymbolTable(context.Globals);

		foreach (var param in func.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is not null)
			{
				var varSymbol = new VariableSymbol(param.Name, paramType, isMutable: false)
				{
					IsInitialized = true,
					Origin = OriginKind.Parameter
				};
				localScope.Declare(varSymbol);
			}
		}

		CheckBlock(func.Body!, localScope, func);

		// Guard: Ensure non-void functions end with a return statement
		if (func.ReturnType != "void" && !EndsWithReturn(func.Body!))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				func.NameSpan,
				$"Function '{func.Name}' is declared to return '{func.ReturnType}' but is missing a return statement."
			);
		}

		_validation.UnsafeDepth = baseUnsafeDepth;
		_validation.InUnbound = baseInUnbound;
		_validation.EnclosingFunction = baseEnclosingFunction;
	}

	private static bool IsUnsafeFunction(FunctionDeclarationSyntax func) =>
		func.Modifier == SafetyTier.Unsafe ||
		func.Attributes.Any(static a => string.Equals(a.Name, "UnsafeBody", StringComparison.OrdinalIgnoreCase));

	private void CheckBlock(BlockStatementSyntax? block, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		if (block is null)
			return;

		_validation.LabelScopes.Push([]);
		try
		{
			foreach (var stmt in block.Statements)
			{
				CheckStatement(stmt, scope, currentFunc);
			}
		}
		finally
		{
			_validation.LabelScopes.Pop();
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
				CheckMustUseDiscard(exprStmt.Expression, scope);
				break;
			case VariableDeclarationSyntax varDecl:
				CheckVariableDeclaration(varDecl, scope, currentFunc);
				break;
			case BlockStatementSyntax block:
				CheckBlock(block, new SymbolTable(scope), currentFunc);
				break;
			case LabeledBlockStatementSyntax labeledBlock:
				{
					var currentScope = _validation.LabelScopes.Count > 0 ? _validation.LabelScopes.Peek() : null;
					if (currentScope is not null && !currentScope.Add(labeledBlock.Label))
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, labeledBlock.Span,
							$"Label '{labeledBlock.Label}' redeclared in the same enclosing scope.",
							DiagnosticIds.DuplicateLabel);
					}

					_validation.BlockLabels.Push(labeledBlock.Label);
					CheckBlock(labeledBlock.Body, new SymbolTable(scope), currentFunc);
					_validation.BlockLabels.Pop();
					break;
				}
			case IfStatementSyntax ifStmt:
				CheckExpression(ifStmt.Condition, scope);
				CheckStatement(ifStmt.ThenStatement, scope, currentFunc);
				if (ifStmt.ElseClause is not null)
					CheckStatement(ifStmt.ElseClause.Body, scope, currentFunc);
				break;
			case WhileStatementSyntax whileStmt:
				EnterLoop(whileStmt.Label, whileStmt.Span);
				CheckExpression(whileStmt.Condition, scope);
				CheckStatement(whileStmt.Body, scope, currentFunc);
				ExitLoop();
				break;
			case ForStatementSyntax forStmt:
				{
					var forScope = new SymbolTable(scope);
					EnterLoop(forStmt.Label, forStmt.Span);
					CheckVariableDeclaration(forStmt.Initializer, forScope, currentFunc);
					CheckExpression(forStmt.Condition, forScope);
					CheckExpression(forStmt.Increment, forScope);
					CheckStatement(forStmt.Body, forScope, currentFunc);
					ExitLoop();
					break;
				}
			case ForEachStatementSyntax forEach:
				{
					var forEachScope = new SymbolTable(scope);
					EnterLoop(forEach.Label, forEach.Span);
					CheckForEachStatement(forEach, forEachScope, currentFunc);
					ExitLoop();
					break;
				}
			case UnsafeBlockStatementSyntax unsafeBlock:
				_validation.UnsafeDepth++;
				CheckBlock(unsafeBlock.Body, new SymbolTable(scope), currentFunc);
				_validation.UnsafeDepth--;
				break;
			case SwitchStatementSyntax sw:
				CheckSwitchStatement(sw, scope, currentFunc);
				break;
			case TryStatementSyntax tryStmt:
				{
					// The compiler driver lowers try/catch/finally before binding, so this case is
					// only reached by the language-server tooling, which binds the original AST.
					CheckBlock(tryStmt.Body, new SymbolTable(scope), currentFunc);
					foreach (var clause in tryStmt.CatchClauses)
					{
						var clauseScope = new SymbolTable(scope);
						if (clause.BindingName is not null && clause.ErrorTypeName is not null &&
							context.ResolveType(clause.ErrorTypeName) is { } bindingType)
						{
							clauseScope.Declare(new VariableSymbol(clause.BindingName, bindingType, isMutable: false)
							{
								IsInitialized = true,
								Origin = OriginKind.Local,
							});
						}

						CheckBlock(clause.Body, clauseScope, currentFunc);
					}

					if (tryStmt.FinallyBody is not null)
						CheckBlock(tryStmt.FinallyBody, new SymbolTable(scope), currentFunc);
					break;
				}
			case BreakStatementSyntax brk:
				CheckControlExit(brk.TargetLabel, brk.Span, isBreak: true);
				break;
			case ContinueStatementSyntax cont:
				CheckControlExit(cont.Label, cont.Span, isBreak: false);
				break;
		}
	}

	private void EnterLoop(string? label, TextSpan span)
	{
		if (label is not null)
		{
			var currentScope = _validation.LabelScopes.Count > 0 ? _validation.LabelScopes.Peek() : null;
			if (currentScope is not null && !currentScope.Add(label))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, span,
					$"Label '{label}' redeclared in the same enclosing scope.",
					DiagnosticIds.DuplicateLabel);
			}
		}

		_validation.LoopLabels.Push(label);
	}

	private void ExitLoop()
	{
		if (_validation.LoopLabels.Count > 0)
			_validation.LoopLabels.Pop();
	}

	private void ReportDiagnostics(TextSpan span, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, span, message, diagnosticId);
	}

	private void CheckForEachStatement(ForEachStatementSyntax forEach, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		CheckExpression(forEach.Collection, scope);
		var collectionType = GetExpressionType(forEach.Collection, scope);

		if (collectionType is null)
			return;

		var underlyingType = collectionType;
		if (underlyingType is PointerTypeSymbol ptr)
			underlyingType = ptr.ReferencedType;

		TypeSymbol? itemType = null;
		bool currentReturnsRef = false;

		if (underlyingType is ArrayTypeSymbol arrayType)
		{
			itemType = arrayType.ElementType;
			forEach.ItemTypeName = itemType.Name;
			forEach.ArraySize = arrayType.Size;
		}
		else if (underlyingType is SliceTypeSymbol sliceType)
		{
			itemType = sliceType.ElementType;
			forEach.ItemTypeName = itemType.Name;
		}
		else
		{
			var typeName = underlyingType.Name;
			var getEnumeratorFunc = ResolveForEachGetEnumerator(typeName, underlyingType, scope, forEach.Collection.Span);

			if (getEnumeratorFunc is null)
				return;

			var enumeratorType = getEnumeratorFunc.ReturnType;
			var enumeratorName = enumeratorType.Name;

			var moveNextName = $"{enumeratorName}.MoveNext";
			var moveNextFunc = Overloads.Resolve(moveNextName, [new PointerTypeSymbol(enumeratorType, isMutable: true)], scope);

			var currentName = $"{enumeratorName}.Current";
			var currentFunc2 = Overloads.Resolve(currentName, [new PointerTypeSymbol(enumeratorType, isMutable: true)], scope);

			if (moveNextFunc is null)
			{
				ReportDiagnostics(forEach.Collection.Span, $"Iterator type '{enumeratorName}' returned by '{typeName}.GetEnumerator()' is invalid: missing a 'bool MoveNext()' method signature.", DiagnosticIds.ForeachMissingMoveNext);
				return;
			}

			if (!moveNextFunc.ReturnType.Equals(TypeSymbol.Bool))
			{
				ReportDiagnostics(forEach.Collection.Span, $"Iterator type '{enumeratorName}' returned by '{typeName}.GetEnumerator()' is invalid: 'MoveNext()' must return a logical 'bool' type scalar.", DiagnosticIds.ForeachMoveNextNotBool);
				return;
			}

			if (currentFunc2 is null)
			{
				ReportDiagnostics(forEach.Collection.Span, $"Iterator type '{enumeratorName}' returned by '{typeName}.GetEnumerator()' is invalid: missing a 'Current' property or method getter.", DiagnosticIds.ForeachMissingCurrent);
				return;
			}

			itemType = currentFunc2.ReturnType;
			forEach.ItemTypeName = itemType is PointerTypeSymbol currentPtr ? currentPtr.ReferencedType.Name : itemType.Name;
			forEach.CurrentReturnsReference = itemType is PointerTypeSymbol;
			currentReturnsRef = itemType is PointerTypeSymbol;
			forEach.GetEnumeratorFunctionName = getEnumeratorFunc.Name;
			forEach.MoveNextFunctionName = moveNextFunc.Name;
			forEach.CurrentFunctionName = currentFunc2.Name;
			forEach.EnumeratorTypeName = enumeratorName;
		}

		if (itemType is null)
			return;

		if (forEach.BindingKind == ForEachVariableKind.RefVar && !(underlyingType is ArrayTypeSymbol || underlyingType is SliceTypeSymbol))
		{
			var resolvedCurrentType = itemType is PointerTypeSymbol p ? p.ReferencedType.Name : itemType.Name;
			if (itemType is PointerTypeSymbol { IsMutable: false })
				ReportDiagnostics(forEach.Collection.Span, $"Cannot bind mutable reference 'refvar {resolvedCurrentType}': the iterator's 'Current' property returns a read-only 'ref T'.", DiagnosticIds.ForeachRefVarReadOnlyRef);
			else if (itemType is not PointerTypeSymbol)
				ReportDiagnostics(forEach.Collection.Span, $"Cannot bind mutable reference 'refvar {resolvedCurrentType}': the iterator's 'Current' property returns by value, yielding no reference address.", DiagnosticIds.ForeachRefVarByValue);
			else
				currentReturnsRef = true;
		}

		if (forEach.ExplicitItemType is not null)
		{
			var declaredType = context.ResolveType(forEach.ExplicitItemType);
			if (declaredType is not null)
			{
				var actualItemType = itemType is PointerTypeSymbol pt ? pt.ReferencedType : itemType;
				if (!declaredType.Equals(actualItemType))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, forEach.Collection.Span, $"Explicit loop item type '{declaredType.Name}' does not match the iterator's underlying 'Current' yield type '{actualItemType.Name}'.", DiagnosticIds.ForeachItemTypeMismatch);
				}
			}
		}

		bool isRefBinding;
		TypeSymbol symbolType;

		if (underlyingType is ArrayTypeSymbol || underlyingType is SliceTypeSymbol)
		{
			isRefBinding = forEach.BindingKind == ForEachVariableKind.RefVar;
			symbolType = isRefBinding ? new PointerTypeSymbol(itemType, isMutable: true) : itemType;
			forEach.IsReferenceBinding = isRefBinding;
			forEach.ItemBindingTypeName = isRefBinding ? $"refvar {itemType.Name}" : itemType.Name;
		}
		else
		{
			bool isVar = forEach.BindingKind == ForEachVariableKind.Var;
			bool isRefVar = forEach.BindingKind == ForEachVariableKind.RefVar;

			if (isVar)
			{
				isRefBinding = false;
				symbolType = currentReturnsRef ? (itemType is PointerTypeSymbol p ? p.ReferencedType : itemType) : itemType;
				forEach.IsReferenceBinding = false;
				forEach.ItemBindingTypeName = symbolType.Name;
			}
			else if (isRefVar)
			{
				isRefBinding = true;
				symbolType = new PointerTypeSymbol(itemType is PointerTypeSymbol p ? p.ReferencedType : itemType, isMutable: true);
				forEach.IsReferenceBinding = true;
				var bindingName = symbolType is PointerTypeSymbol sp2 ? sp2.ReferencedType.Name : symbolType.Name;
				forEach.ItemBindingTypeName = $"refvar {bindingName}";
			}
			else if (currentReturnsRef)
			{
				isRefBinding = true;
				symbolType = new PointerTypeSymbol(itemType is PointerTypeSymbol p ? p.ReferencedType : itemType, isMutable: false);
				forEach.IsReferenceBinding = true;
				var bindingName = symbolType is PointerTypeSymbol sp3 ? sp3.ReferencedType.Name : symbolType.Name;
				forEach.ItemBindingTypeName = $"refvar {bindingName}";
			}
			else
			{
				isRefBinding = false;
				symbolType = itemType;
				forEach.IsReferenceBinding = false;
				forEach.ItemBindingTypeName = itemType.Name;
			}
		}

		var existing = scope.Lookup(forEach.ItemName);
		if (existing is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, forEach.Collection.Span, $"Variable '{forEach.ItemName}' is already declared in this scope");
		}

		bool isMutable = forEach.BindingKind == ForEachVariableKind.Var || forEach.BindingKind == ForEachVariableKind.RefVar;
		var itemSymbol = new VariableSymbol(forEach.ItemName, symbolType, isMutable);
		scope.Declare(itemSymbol);
		context.VariableSymbols[new VariableDeclarationSyntax(forEach.Span, isMutable, forEach.ItemBindingTypeName, forEach.ItemName, null)] = itemSymbol;

		_validation.ReadOnlyForeachItems.Push(forEach.BindingKind == ForEachVariableKind.Var ? [] : new HashSet<string> { forEach.ItemName });
		try
		{
			CheckStatement(forEach.Body, scope, currentFunc);
		}
		finally
		{
			_validation.ReadOnlyForeachItems.Pop();
		}
	}

	private FunctionSymbol? ResolveForEachGetEnumerator(string typeName, TypeSymbol collectionType, SymbolTable scope, TextSpan span)
	{
		var name = $"{typeName}.GetEnumerator";
		var receiverTypes = new[] { new PointerTypeSymbol(collectionType, isMutable: true) };

		var candidates = new List<FunctionSymbol>();
		Overloads.GatherCandidates(name, candidates);

		// Overload candidate discovery can surface the same physical symbol more than once
		// (exact-name, current-namespace, and active-using lookups may all hit the same
		// entry). Deduplicate by identity so a single candidate is never scored as a tie.
		candidates = candidates.Distinct().ToList();

		if (candidates.Count == 0)
		{
			ReportDiagnostics(span, $"Type '{typeName}' cannot be traversed via foreach: 'GetEnumerator()' method is missing.", DiagnosticIds.ForeachNoGetEnumerator);
			return null;
		}

		// §4.B Visibility matrix: a present-but-inaccessible structural method is a distinct failure
		// (CVL1090) reported instead of the terminal CVL1080. Internal methods are always reachable
		// inside a single module; private methods require the identical source file (unit).
		if (!context.LegacyVisibility)
		{
			var visible = candidates
				.Where(c => c.DeclaringUnit is null || c.DeclaringUnit == context.CurrentUnit ||
							VisibilityChecker.IsAccessible(c.Visibility, context.CurrentUnit, c.DeclaringUnit))
				.ToList();
			if (visible.Count == 0)
			{
				ReportDiagnostics(span, $"Type '{typeName}' cannot be traversed via foreach: 'GetEnumerator()' method is inaccessible due to its protection level.", DiagnosticIds.ForeachInaccessibleGetEnumerator);
				return null;
			}
			candidates = visible;
		}

		// Score every overload against the structural no-arg receiver shape. A tie between distinct
		// candidates is the ambiguous-routing failure (CVL1089).
		FunctionSymbol? best = null;
		var bestScore = -1;
		var tieCount = 0;

		foreach (var candidate in candidates)
		{
			var paramTypes = candidate.Parameters.Select(p => p.Type).ToList();
			var score = Overloads.CompareSignature(paramTypes, receiverTypes, candidate.IsVariadic);
			if (score > bestScore)
			{
				bestScore = score;
				best = candidate;
				tieCount = 1;
			}
			else if (score == bestScore && score >= 0)
			{
				tieCount++;
			}
		}

		if (bestScore < 0 || best is null)
		{
			ReportDiagnostics(span, $"Type '{typeName}' cannot be traversed via foreach: 'GetEnumerator()' method is missing.", DiagnosticIds.ForeachNoGetEnumerator);
			return null;
		}

		if (tieCount > 1)
		{
			ReportDiagnostics(span, $"Ambiguous iteration routing: type '{typeName}' exposes multiple conflicting overloads for 'GetEnumerator()'.", DiagnosticIds.ForeachAmbiguousGetEnumerator);
			return null;
		}

		return best;
	}

	private void CheckControlExit(string? label, TextSpan span, bool isBreak)
	{
		if (label is null)
		{
			var inLoop = _validation.LoopLabels.Count > 0;
			var inSwitch = _validation.SwitchDepth > 0;

			// An unlabeled break is legal inside a switch case (C-style switch exit) even with
			// no enclosing loop; continue is strictly a loop construct. This version requires a
			// label for any other break target, so a bare break outside a loop/switch is CVL1066.
			if (!inLoop && !(isBreak && inSwitch))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, span,
					isBreak
						? "`break` requires a label in this version."
						: "The unstructured iteration statement 'break' or 'continue' can only be executed inside an active loop or switch-case body context.",
					isBreak ? DiagnosticIds.BreakRequiresLabel : DiagnosticIds.LoopControlOutsideLoop);
			}

			return;
		}

		var foundLoop = _validation.LoopLabels.Contains(label);
		if (foundLoop)
			return;

		if (isBreak && _validation.BlockLabels.Contains(label))
			return;

		var reportContext = context.FileContexts[context.CurrentUnit!];
		if (isBreak)
		{
			context.Diagnostics.Report(reportContext, span,
				$"`break {label};` refers to a label `{label}` not in scope.",
				DiagnosticIds.LabelNotFoundInScope);
		}
		else
		{
			context.Diagnostics.Report(reportContext, span,
				$"`continue {label};` refers to a label `{label}` that is not an enclosing loop.",
				DiagnosticIds.LabelNotFoundInScope);
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

		// Delegate-typed declarations: a lambda or function/method-group initializer is
		// target-typed against the declared delegate (§4.2 / §22). Without an expected
		// delegate type a lambda has no standalone type and is rejected.
		var declaredDelegateType = varDecl.Type is "ref" or "refvar"
			? null
			: varDecl.Type is null ? null : context.ResolveType(varDecl.Type) as DelegateTypeSymbol;

		if (declaredDelegateType is not null)
		{
			if (varDecl.Initializer is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, varDecl.Span,
					$"Delegate '{declaredDelegateType.Name}' requires an initializer; delegates are non-null and cannot be default-initialized.",
					DiagnosticIds.DelegateNotDefaultInitializable);
			}
			else if (varDecl.Initializer is LambdaExpressionSyntax targetLambda)
			{
				CheckTargetTypedLambda(targetLambda, declaredDelegateType, scope);
			}
			else if (varDecl.Initializer is NullLiteralExpressionSyntax)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, varDecl.Initializer.Span,
					$"Cannot initialize delegate '{declaredDelegateType.Name}' with 'null'; delegates are non-null values.",
					DiagnosticIds.NullLiteralForDelegate);
			}
			else if (varDecl.Initializer is IdentifierExpressionSyntax groupId && !Calls.IsKnownVariable(groupId, scope) && Overloads.HasCandidates(groupId.Name))
			{
				CheckFunctionGroupConversion(groupId, declaredDelegateType, scope);
			}
			else if (varDecl.Initializer is MemberAccessExpressionSyntax groupMa && IsMethodGroupReference(groupMa, scope))
			{
				CheckFunctionGroupConversion(groupMa, declaredDelegateType, scope);
			}
			else
			{
				CheckExpression(varDecl.Initializer, scope);
			}

			resolvedType = declaredDelegateType;
		}
		else if (varDecl.Initializer is not null)
		{
			if (varDecl.Initializer is LambdaExpressionSyntax bareLambda)
			{
				// A lambda in a non-delegate context has no expected delegate type (spec §4.2).
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, bareLambda.Span,
					"Lambda requires an expected delegate type. Declare the variable with an explicit delegate type.",
					DiagnosticIds.LambdaRequiresExpectedDelegateType);
			}
			else
			{
				CheckExpression(varDecl.Initializer, scope);
				resolvedType = GetExpressionType(varDecl.Initializer, scope);
			}
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

			// Array types with computed sizes (e.g. int[Color.Max + 1]) must fully resolve;
			// a failure means the size expression referenced an unknown/invalid constant.
			if (resolvedType is null && varDecl.Type.Contains('[') && !varDecl.Type.Contains("[]"))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, varDecl.Span, $"Cannot resolve type '{varDecl.Type}'.");
			}

			var initializerType = declaredDelegateType is null && varDecl.Initializer != null ? GetExpressionType(varDecl.Initializer, scope) : null;

			// Implicit Dereference: If target is value but initializer is a pointer, unwrap it
			if (initializerType is PointerTypeSymbol ptr && resolvedType is not PointerTypeSymbol)
			{
				initializerType = ptr.ReferencedType;
			}

			if (resolvedType != null && initializerType != null && !resolvedType.Equals(initializerType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];

				var isValidNull = initializerType.Equals(TypeSymbol.Null) &&
								  (resolvedType is RawPointerTypeSymbol ||
								  (resolvedType is UnionTypeSymbol union && union.IsOption));

				// Integer width family: implicit conversion between exact-width integers
				// (byte/short/int/long and unsigned variants) is allowed.
				var isIntegerWidthConversion = TypeSymbol.IsNumericIntegerType(resolvedType)
					&& TypeSymbol.IsNumericIntegerType(initializerType);

				// Implicit float -> double widening is allowed; the reverse needs an
				// explicit cast (a bare `double` literal to `float` is a dedicated error).
				var isFloatWidening = TypeSymbol.IsFloatingPointType(resolvedType)
					&& TypeSymbol.IsFloatingPointType(initializerType)
					&& resolvedType.Equals(TypeSymbol.Double);

				if (initializerType.Equals(TypeSymbol.Null) && _validation.UnsafeDepth > 0)
				{
					// In safe/unbound code the blanket 'null is not allowed' (CVL1104)
					// from SafetyPass applies; these pointer-shape rules only matter
					// inside unsafe contexts where null is a real value.
					if (resolvedType is PointerTypeSymbol)
					{
						context.Diagnostics.Report(currentFileContext, varDecl.Span, "Cannot assign `null` to a safe reference (`ref` / `refvar`).", DiagnosticIds.NullToSafeReference);
					}
					else if (!isValidNull)
					{
						context.Diagnostics.Report(currentFileContext, varDecl.Span, "The 'null' literal requires a pointer type (Option or raw pointer).", DiagnosticIds.NullOutsideUnsafeContext);
					}
				}
				else if (!isIntegerWidthConversion && !isFloatWidening)
				{
					if (resolvedType.Equals(TypeSymbol.Float) && varDecl.Initializer is DoubleLiteralExpressionSyntax)
					{
						context.Diagnostics.Report(currentFileContext, varDecl.Span, "Cannot implicitly convert `double` literal to `float`. Use `f` suffix.", DiagnosticIds.DoubleLiteralToFloatAssignment);
					}
					else
					{
						context.Diagnostics.Report(currentFileContext, varDecl.Span, $"Cannot initialize variable of type '{resolvedType.Name}' with value of type '{initializerType.Name}'");
					}
				}
			}

			// CVL1902: an integer literal must fit the declared numeric integer type.
			if (varDecl.Initializer is IntegerLiteralExpressionSyntax intLiteral
				&& resolvedType is not null
				&& TypeSymbol.IsNumericIntegerType(resolvedType))
			{
				var width = TypeSymbol.IntegerBitWidth(resolvedType);
				var maxValue = TypeSymbol.IsSignedIntegerType(resolvedType)
					? (1UL << (width - 1)) - 1
					: width == 64 ? ulong.MaxValue : (1UL << width) - 1;

				if (intLiteral.Value > maxValue)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					var literalText = currentFileContext.Source.Substring(intLiteral.Span.Start, intLiteral.Span.Length);
					context.Diagnostics.Report(currentFileContext, varDecl.Span,
						$"Integer literal `{literalText}` is too large for type `{resolvedType.Name}`.", DiagnosticIds.IntegerLiteralTooLarge);
				}
			}
		}

		resolvedType ??= TypeSymbol.Int;

		// Stack Allocation Guard: block any single local array whose byte size would
		// exceed the 1 MB safety threshold (spec §5.C.2).
		if (resolvedType is ArrayTypeSymbol stackArray && StackByteSize(stackArray) > 1_048_576)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, varDecl.Span, "Array size exceeds stack allocation safety threshold");
		}

		var varSymbol = new VariableSymbol(varDecl.Name, resolvedType, varDecl.IsMutable)
		{
			IsInitialized = varDecl.Initializer != null,
			IsHeapAllocated = varDecl.Initializer is HeapAllocationExpressionSyntax or HeapArrayAllocationExpressionSyntax
		};

		scope.Declare(varSymbol);
		context.VariableSymbols[varDecl] = varSymbol;
	}

	private static int StackByteSize(TypeSymbol type) => type switch
	{
		ArrayTypeSymbol arr => StackByteSize(arr.ElementType) * arr.Size,
		PointerTypeSymbol => 8,
		RawPointerTypeSymbol => 8,
		SliceTypeSymbol => 16,
		EnumTypeSymbol enumType => StackByteSize(enumType.StorageType),
		StructTypeSymbol structType => structType.Fields.Sum(f => StackByteSize(f.Type)),
		UnionTypeSymbol unionType => unionType.IsOption && unionType.IsNpoEligible
			? 8
			: 1 + (unionType.Fields.Count == 0 ? 0 : unionType.Fields.Max(f => StackByteSize(f.Type))),
		_ => TypeSymbol.PrimitiveByteSize(type),
	};

	private void CheckExpression(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				{
					var symbol = scope.Lookup(id.Name);
					if (symbol is null)
					{
						var resolvedGlobal = context.ResolveGlobalReference(id.Name, out var ambiguousCandidates);
						if (ambiguousCandidates is not null)
						{
							var currentFileContext2 = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(currentFileContext2, id.Span,
								$"Reference to '{id.Name}' is ambiguous between '{string.Join("' and '", ambiguousCandidates)}'.",
								DiagnosticIds.AmbiguousGlobalReference);
						}
						else if (resolvedGlobal is null)
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(currentFileContext, id.Span, $"Undefined variable '{id.Name}'");
						}
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
				foreach (var el in arrInit.Elements)
					CheckExpression(el, scope);
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
					// First evaluate argument types at the call site. Lambda expressions and
					// function/method-group references have no type of their own: they are
					// deferred and target-typed once the callee is resolved.
					var argTypes = new List<TypeSymbol>();
					var deferredGroupArgs = new List<(ExpressionSyntax Arg, int Index)>();
					for (var argIndex = 0; argIndex < call.Arguments.Count; argIndex++)
					{
						var arg = call.Arguments[argIndex];
						if (arg is LambdaExpressionSyntax)
						{
							deferredGroupArgs.Add((arg, argIndex));
							argTypes.Add(OverloadResolver.DeferredCallableArgument);
							continue;
						}

						var isDeferredGroup = arg switch
						{
							IdentifierExpressionSyntax idArg =>
								!Calls.IsKnownVariable(idArg, scope) && Overloads.HasCandidates(idArg.Name),
							MemberAccessExpressionSyntax maArg => IsMethodGroupReference(maArg, scope),
							_ => false,
						};
						if (isDeferredGroup)
						{
							deferredGroupArgs.Add((arg, argIndex));
							argTypes.Add(OverloadResolver.DeferredCallableArgument);
							continue;
						}

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
							func = Overloads.Resolve(resolvedType.Name, argTypes, scope, call);
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
						func = Calls.ResolveOrdinaryCall(call, argTypes, scope);

						// No concrete overload matched: fall back to interface-parameterized dispatch
						// (implicit generic templates monomorphized with the concrete conforming arg types).
						if (func is null && ResolveInterfaceFunctionTemplateName(call.FunctionName, scope) is not null)
						{
							// The callee is an interface template: specific conformance/arg-count
							// diagnostics are reported inside. Return early so the generic
							// "no overload" message is not also emitted.
							func = TryResolveInterfaceCall(call, argTypes, scope);
							if (func is null)
								return;
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
							if (func is null)
								return;
						}
					}

					if (func is null)
					{
						// Not an ordinary function: could this be a delegate value invocation
						// ('h(42)') or a delegate-typed field invocation ('obj.Handler(42)')?
						if (Calls.TryResolveDelegateInvocation(call, scope, out var delegateType))
						{
							context.ResolvedDelegateCalls[call] = delegateType;

							if (deferredGroupArgs.Count > 0)
							{
								for (var i = 0; i < call.Arguments.Count; i++)
								{
									if (i >= delegateType.Parameters.Count)
										break;
									foreach (var (arg, argIndex) in deferredGroupArgs)
									{
										if (argIndex != i)
											continue;
										if (delegateType.Parameters[i].Type is not DelegateTypeSymbol paramDelegateTy)
											continue;
										if (arg is LambdaExpressionSyntax lam)
											CheckTargetTypedLambda(lam, paramDelegateTy, scope);
										else
											CheckFunctionGroupConversion(arg, paramDelegateTy, scope);
									}
								}
							}

							break;
						}

						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						var sigString = string.Join(", ", argTypes.Select(t => t.Name));
						context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan, $"No overload of function '{call.FunctionName}' matches argument types ({sigString})");
						return;
					}

					// Record the resolved overload for CodeGenerator consumption
					context.ResolvedCalls[call] = func;

					// Target-type any deferred lambda/group arguments against the callee's
					// parameter declarations (§4.2 contextual lambda typing, §22 group conversion).
					if (deferredGroupArgs.Count > 0)
					{
						var isExtensionForDeferred = func.Parameters.Count > 0 && func.Parameters[0].Name == "this";
						foreach (var (arg, argIndex) in deferredGroupArgs)
						{
							var paramIndex = isExtensionForDeferred ? argIndex + 1 : argIndex;
							if (paramIndex >= func.Parameters.Count)
								continue;
							var paramType = func.Parameters[paramIndex].Type;

							if (paramType is DelegateTypeSymbol delegateParamType)
							{
								if (arg is LambdaExpressionSyntax lam)
									CheckTargetTypedLambda(lam, delegateParamType, scope);
								else
									CheckFunctionGroupConversion(arg, delegateParamType, scope);
							}
							else
							{
								var currentFileContext = context.FileContexts[context.CurrentUnit!];
								context.Diagnostics.Report(currentFileContext, arg.Span,
									"Lambda requires an expected delegate type; the corresponding parameter is not a delegate.",
									DiagnosticIds.LambdaRequiresExpectedDelegateType);
							}
						}
					}

					// Caller-side unsafe invocation check (Memory & Safety spec §6.A): a raw 'unsafe fn'
					// (form A) must be invoked from an unsafe context. '[UnsafeBody]' functions expose a safe
					// API (form B) and are exempt, as are calls already inside an unsafe context.
					// Enforce CVL1009: Calling a raw 'unsafe function' from code that is not in an unsafe context
					// Note: [UnsafeBody] functions are encapsulated and exempt from this call-site restriction.
					if (func.SafetyTier == SafetyTier.Unsafe && !func.IsUnsafeBody && _validation.UnsafeDepth == 0)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(
							currentFileContext,
							call.Span,
							$"Calling a raw 'unsafe function' '{call.FunctionName}' from code that is not in an unsafe context.",
							DiagnosticIds.CallUnsafeFromSafe);
					}

					var argCount = call.Arguments.Count;
					var paramCount = func.Parameters.Count;
					var isVariadic = func.IsVariadic;

					var isExtensionCall = func.Parameters.Count > 0 && func.Parameters[0].Name == "this";
					var expectedParamCount = isExtensionCall ? paramCount - 1 : paramCount;

					if (!isVariadic && argCount != expectedParamCount)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan, $"Function '{call.FunctionName}' expects {expectedParamCount} arguments but received {argCount}");
						return;
					}

					if (isVariadic && argCount < paramCount)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan, $"Function '{call.FunctionName}' expects at least {paramCount} arguments but received {argCount}");
						return;
					}

					if (!isVariadic)
					{
						for (var i = 0; i < call.Arguments.Count; i++)
						{
							var paramIndex = isExtensionCall ? i + 1 : i;
							if (paramIndex >= func.Parameters.Count)
								break;
							CheckLargeUnionByValueArgument(call.Arguments[i], func.Parameters[paramIndex].Type, scope);
						}
					}

					break;
				}
			case BinaryExpressionSyntax bin:
				{
					if (bin.Operator == "=")
					{
						// Handle discard assignment: _ = Func();
						if (bin.Left is IdentifierExpressionSyntax discardId && discardId.Name == "_")
						{
							CheckExpression(bin.Right, scope);
							break;
						}

						// Delegate-typed assignment targets: a lambda / function-group RHS is
						// target-typed against the assigned variable's delegate type (§4.2 / §22).
						if (bin.Left is IdentifierExpressionSyntax targetId &&
							((scope.Lookup(targetId.Name) as VariableSymbol) ?? context.ResolveGlobalReference(targetId.Name, out _)) is { Type: DelegateTypeSymbol assigneeDelegate })
						{
							if (bin.Right is LambdaExpressionSyntax assignLambda)
							{
								CheckTargetTypedLambda(assignLambda, assigneeDelegate, scope);
								break;
							}
							if (bin.Right is IdentifierExpressionSyntax assignGroupId && !Calls.IsKnownVariable(assignGroupId, scope) && Overloads.HasCandidates(assignGroupId.Name))
							{
								CheckFunctionGroupConversion(assignGroupId, assigneeDelegate, scope);
								break;
							}
							if (bin.Right is MemberAccessExpressionSyntax assignGroupMa && IsMethodGroupReference(assignGroupMa, scope))
							{
								CheckFunctionGroupConversion(assignGroupMa, assigneeDelegate, scope);
								break;
							}
						}

						// 1. Evaluate the right-hand side first (reads and moves happen here)
						CheckExpression(bin.Right, scope);

						// 2. Evaluate the left-hand side second (re-initialization happens here)
						if (bin.Left is IdentifierExpressionSyntax id)
						{
							var varSymbol = scope.Lookup(id.Name) as VariableSymbol
								?? context.ResolveGlobalReference(id.Name, out _);
							if (varSymbol is not null)
							{
								var isMutable = varSymbol.IsMutable || (varSymbol.Type is PointerTypeSymbol ptr && ptr.IsMutable);
								if (!isMutable)
								{
									var currentFileContext = context.FileContexts[context.CurrentUnit!];
									if (_validation.ReadOnlyForeachItems.Count > 0 && _validation.ReadOnlyForeachItems.Peek().Contains(id.Name))
									{
										context.Diagnostics.Report(currentFileContext, id.Span,
																				$"The loop variable '{id.Name}' is read-only and cannot be reassigned inside the execution block.",
																				DiagnosticIds.ForeachReadOnlyAssignment);
									}
									else
									{
										context.Diagnostics.Report(currentFileContext, id.Span, $"Cannot assign to immutable variable '{id.Name}'");
									}
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

						var binLeftType = GetExpressionType(bin.Left, scope);
						var binRightType = GetExpressionType(bin.Right, scope);
						if (bin.Operator == "+" &&
							((binLeftType?.Equals(TypeSymbol.String) ?? false) || (binRightType?.Equals(TypeSymbol.String) ?? false)) &&
							(!IsConstantStringExpression(bin.Left) || !IsConstantStringExpression(bin.Right)))
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(currentFileContext, bin.Span,
								"The `+` operator on strings is only allowed between compile-time constant strings (literals).",
								DiagnosticIds.DynamicStringConcatenation);
						}
					}

					break;
				}
			case AsmExpressionSyntax asmExpr:
				CheckAsmExpression(asmExpr, scope);
				break;
			case NameofExpressionSyntax nameofExpr:
				CheckNameofExpression(nameofExpr, scope);
				break;
			case TypeofExpressionSyntax typeofExpr:
				CheckTypeofExpression(typeofExpr, scope);
				break;
			case UnaryExpressionSyntax unary:
				CheckExpression(unary.Operand, scope);
				CheckUnaryCast(unary, scope);
				CheckUnaryEnumTilde(unary, scope);
				break;
			case IsPatternExpressionSyntax isPat:
				CheckIsPatternExpression(isPat, scope);
				break;
			case VoidLiteralExpressionSyntax:
				break;
			case LambdaExpressionSyntax strayLambda:
				// A lambda has no standalone type; if it reaches this point without being
				// target-typed by a caller (declaration, assignment, return, or argument),
				// there is no expected DelegateTypeSymbol to bind against (§4.2).
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, strayLambda.Span,
						"Lambda requires an expected delegate type.",
						DiagnosticIds.LambdaRequiresExpectedDelegateType);
				}
				break;
			case DefaultExpressionSyntax defaultExpr:
				{
					if (defaultExpr.TypeName is null)
					{
						// Bare 'default' (no type argument) is only lowered by OptionalSyntaxRewriter
						// inside a typed declaration; anywhere else there is no type to infer.
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, defaultExpr.Span,
							"Cannot infer the type of a bare 'default' expression. Use default(T) or declare the variable with an explicit type.");
						break;
					}

					var defaultTy = context.ResolveType(defaultExpr.TypeName);
					if (defaultTy is null)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, defaultExpr.Span, $"Unknown type '{defaultExpr.TypeName}' in default expression");
					}
					else if (defaultTy is TypeParameterSymbol)
					{
						// Generic type parameters (e.g., default(A) where A is a generic parameter) are always allowed.
						break;
					}
					else if (defaultTy is StructTypeSymbol or UnionTypeSymbol)
					{
						var defaultKind = Classification.Classify(defaultTy);
						if (defaultKind != CopyKind.TrivialCopy)
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(currentFileContext, defaultExpr.Span, $"Type '{defaultExpr.TypeName}' cannot be used with default because it is not a Trivial Copy Type");
						}
					}
					else if (defaultTy is DelegateTypeSymbol)
					{
						// Safe delegates are non-null / non-default-initializable (§16).
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, defaultExpr.Span,
							$"Type '{defaultExpr.TypeName}' is a delegate and cannot be default-initialized; delegates are non-null values and require an initializer (function, lambda, or method group).",
							DiagnosticIds.DelegateNotDefaultInitializable);
					}
				}

				break;
		}
	}

	private TypeSymbol? CheckMemberAccessExpression(MemberAccessExpressionSyntax expr, SymbolTable scope)
	{
		// Namespace-qualified global access: Ns.Sub.Member. Resolved before the receiver is
		// checked as an expression so 'Math' is not reported as an undefined variable.
		if (TryResolveNamespaceGlobal(expr, out var globalSymbol))
		{
			return globalSymbol.Type;
		}

		// Enum scoped-variant access: EnumName.Variant (optionally namespaced). The
		// receiver is a *type name*, not a value expression — resolve it before the
		// scope lookup so the receiver is not reported as an undefined variable.
		if (TryResolveEnumVariantReceiver(expr) is { } enumType)
		{
			var variant = enumType.FindVariant(expr.MemberName);
			if (variant is null)
			{
				// Enum metaprogramming constants (spec §5): Min, Max, Count are
				// compile-time integers; Values is a read-only slice of the enum.
				if (expr.MemberName is "Min" or "Max" or "Count")
					return TypeSymbol.Int;
				if (expr.MemberName == "Values")
					return new SliceTypeSymbol(enumType);

				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span,
					$"Enum '{enumType.Name}' does not contain variant '{expr.MemberName}'");
				return null;
			}

			return enumType;
		}

		CheckExpression(expr.Expression, scope);
		var leftType = GetExpressionType(expr.Expression, scope);
		if (leftType is null)
			return null;

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

			if (!context.LegacyVisibility && !VisibilityChecker.IsAccessible(variantField.Visibility, context.CurrentUnit, GetDeclaringUnit(unionType)) &&
				!(_validation.InUnbound && variantField.Type is PointerTypeSymbol))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span,
					$"Member '{expr.MemberName}' on type '{unionType.Name}' is inaccessible due to its visibility level.", DiagnosticIds.InaccessibleMember);
				return variantField.Type;
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

		if (!context.LegacyVisibility && !VisibilityChecker.IsAccessible(field.Visibility, context.CurrentUnit, GetDeclaringUnit(structType)) &&
			!(_validation.InUnbound && field.Type is PointerTypeSymbol))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span,
				$"Member '{expr.MemberName}' on type '{structType.Name}' is inaccessible due to its visibility level.", DiagnosticIds.InaccessibleMember);
		}

		return field.Type;
	}

	/// <summary>
	/// True when the member access is a namespace-qualified reference to a global variable
	/// (<c>Ns.Sub.Member</c>); returns the resolved symbol. Only identifier chains are treated
	/// as namespace paths — value receivers (struct fields, unions, etc.) fall through.
	/// </summary>
	private bool TryResolveNamespaceGlobal(MemberAccessExpressionSyntax expr, out VariableSymbol? globalSymbol)
	{
		globalSymbol = null;
		if (expr.Expression is not (IdentifierExpressionSyntax or MemberAccessExpressionSyntax))
			return false;

		var segments = new List<string>();
		var current = expr.Expression;
		while (current is MemberAccessExpressionSyntax memberAccess)
		{
			if (memberAccess.Expression is not (IdentifierExpressionSyntax or MemberAccessExpressionSyntax))
				return false;
			segments.Add(memberAccess.MemberName);
			current = memberAccess.Expression;
		}

		if (current is not IdentifierExpressionSyntax leaf)
			return false;
		segments.Add(leaf.Name);
		segments.Reverse();

		globalSymbol = context.ResolveQualifiedGlobal(string.Join(".", segments), expr.MemberName);
		return globalSymbol is not null;
	}

	private CompilationUnitSyntax? GetDeclaringUnit(TypeSymbol type)
	{
		foreach (var name in ExpandTemplateNames(type))
		{
			if (context.SymbolUnits.TryGetValue(name, out var unit))
				return unit;
		}

		return null;
	}

	private static IEnumerable<string> ExpandTemplateNames(TypeSymbol type)
	{
		yield return type.Name;
		var name = type.Name;
		var lt = name.IndexOf('<');
		if (lt > 0)
			yield return name[..lt];
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

	/// <summary>
	/// Whether a value of type <paramref name="source"/> may be used where type
	/// <paramref name="target"/> is expected. Beyond exact equality, permits the safe
	/// refvar→ref downcast (dropping mutability of a reference to the same type).
	/// </summary>
	private static bool TypesAssignable(TypeSymbol target, TypeSymbol source)
	{
		if (target.Equals(source))
			return true;

		if (source is PointerTypeSymbol srcPtr && target is PointerTypeSymbol tgtPtr &&
			!tgtPtr.IsMutable && srcPtr.IsMutable &&
			srcPtr.ReferencedType.Equals(tgtPtr.ReferencedType))
			return true;

		return false;
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

			if (!context.LegacyVisibility && field.Visibility == Visibility.Private && !VisibilityChecker.IsAccessible(field.Visibility, context.CurrentUnit, GetDeclaringUnit(unionType)))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span,
					$"Cannot initialize private field '{init.MemberName}' using an external struct literal. Use an authorized constructor within the type's defining package module boundary.", DiagnosticIds.PrivateFieldLiteralInit);
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
			if (initType is not null && !TypesAssignable(field.Type, initType))
			{
				var isValidNull = initType.Equals(TypeSymbol.Null) &&
								  (field.Type is RawPointerTypeSymbol ||
								  (field.Type is UnionTypeSymbol union && union.IsOption));

				if (!isValidNull)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					if (_validation.UnsafeDepth > 0 && initType.Equals(TypeSymbol.Null))
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

			if (!context.LegacyVisibility && field.Visibility == Visibility.Private && !VisibilityChecker.IsAccessible(field.Visibility, context.CurrentUnit, GetDeclaringUnit(structType)))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span,
					$"Cannot initialize private field '{init.MemberName}' using an external struct literal. Use an authorized constructor within the type's defining package module boundary.", DiagnosticIds.PrivateFieldLiteralInit);
			}

			if (init.Expression is ParenthesizedStructInitializerExpressionSyntax nested)
			{
				nested.ResolvedStructTypeName = field.Type.Name;
				CheckParenthesizedStructInitialization(nested, scope);
			}
			else if (field.Type is DelegateTypeSymbol delegateFieldType)
			{
				CheckDelegateValueExpression(init.Expression, delegateFieldType, scope);
			}
			else
			{
				CheckExpression(init.Expression, scope);
			}

			// Delegate fields are target-typed against the declared member type; the
			// group/lambda check above already verified assignability.
			var initType = field.Type is DelegateTypeSymbol ? null : GetExpressionType(init.Expression, scope);
			if (initType is not null && !TypesAssignable(field.Type, initType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span, $"Cannot initialize field '{init.MemberName}' of type '{field.Type.Name}' with value of type '{initType.Name}'");
			}
		}

		foreach (var field in structType.Fields)
		{
			if (!initializedFields.Contains(field.Name))
			{
				// Rule 10 (Deferred Reference Initialization): inside an unbound context, reference
				// fields (`ref`/`refvar`) that point to self-referential structures are exempted from
				// strict immediate-initialization; they are filled in subsequently within the unbound body.
				if ((_validation.InUnbound || _validation.UnsafeDepth > 0) && field.Type is PointerTypeSymbol)
					continue;

				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span, $"Missing initializer for field '{field.Name}' of struct '{structType.Name}'");
			}
		}

		return structType;
	}

	private TypeSymbol? CheckArrayInitialization(ArrayInitializationExpressionSyntax expr, SymbolTable scope)
	{
		if (expr.Elements.Count == 0)
			return null; // Can't infer type of empty array easily yet

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
		if (innerType is null)
			return null;

		var isVariableMutable = IsExpressionMutable(expr.Expression, scope);

		if (expr.IsMutable && !isVariableMutable)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, "Cannot take a mutable reference (refvar) of a read-only variable.");
		}

		return new PointerTypeSymbol(innerType, expr.IsMutable);
	}

	private bool IsExpressionMutable(ExpressionSyntax expr, SymbolTable scope)
	{
		// 1. In unsafe context / [UnsafeBody], raw pointer dereferences are mutable l-values
		if (_validation.UnsafeDepth > 0 && expr is UnaryExpressionSyntax { Operator: "*" })
			return true;

		if (expr is UnaryExpressionSyntax { Operator: "*" } deref)
		{
			var opType = GetExpressionType(deref.Operand, scope);
			return opType is RawPointerTypeSymbol || (opType is PointerTypeSymbol ptr && ptr.IsMutable);
		}

		if (expr is IdentifierExpressionSyntax id)
		{
			var symbol = scope.Lookup(id.Name) as VariableSymbol
				?? context.ResolveGlobalReference(id.Name, out _);
			if (symbol is not null)
			{
				return symbol.IsMutable || (symbol.Type is PointerTypeSymbol ptr && ptr.IsMutable);
			}

			if (scope.Lookup("this") is VariableSymbol thisSymbol)
			{
				return thisSymbol.Type is PointerTypeSymbol thisPtr && thisPtr.IsMutable;
			}
		}

		if (expr is MemberAccessExpressionSyntax m)
		{
			var targetType = GetExpressionType(m.Expression, scope);
			if (targetType is RawPointerTypeSymbol)
				return true;
			if (targetType is PointerTypeSymbol ptr)
				return ptr.IsMutable;

			return IsExpressionMutable(m.Expression, scope);
		}

		if (expr is IndexExpressionSyntax idx)
		{
			var parentType = GetExpressionType(idx.Left, scope);
			if (parentType is SliceTypeSymbol or RawPointerTypeSymbol)
				return true;

			return IsExpressionMutable(idx.Left, scope);
		}

		return false;
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
		if (ret.Expression is null)
			return;

		var expectedType = _validation.LambdaReturnType ?? context.ResolveType(currentFunc.ReturnType);

		// A lambda (or function group) returned from a delegate-typed function is
		// target-typed against the expected delegate; skip the generic expression check.
		if (expectedType is DelegateTypeSymbol expectedDelegate)
		{
			if (ret.Expression is LambdaExpressionSyntax retLambda)
			{
				CheckTargetTypedLambda(retLambda, expectedDelegate, scope);
				return;
			}

			if (ret.Expression is IdentifierExpressionSyntax retId && !Calls.IsKnownVariable(retId, scope) && Overloads.HasCandidates(retId.Name))
			{
				CheckFunctionGroupConversion(retId, expectedDelegate, scope);
				return;
			}

			if (ret.Expression is MemberAccessExpressionSyntax retMa && IsMethodGroupReference(retMa, scope))
			{
				CheckFunctionGroupConversion(retMa, expectedDelegate, scope);
				return;
			}
		}

		CheckExpression(ret.Expression, scope);

		var actualType = GetExpressionType(ret.Expression, scope);

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
					if (_validation.UnsafeDepth > 0 && actualType.Equals(TypeSymbol.Null))
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
			IdentifierExpressionSyntax id => (scope.Lookup(id.Name) as VariableSymbol)?.Type ?? context.ResolveGlobalReference(id.Name, out _)?.Type,
			IntegerLiteralExpressionSyntax intLit => intLit.LiteralType switch
			{
				"uint" => TypeSymbol.UInt,
				"long" => TypeSymbol.Long,
				"ulong" => TypeSymbol.ULong,
				_ => intLit.Value <= (ulong)int.MaxValue ? TypeSymbol.Int : TypeSymbol.Long,
			},
			DoubleLiteralExpressionSyntax dblLit => dblLit.IsFloat ? TypeSymbol.Float : TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			NullLiteralExpressionSyntax => TypeSymbol.Null,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			CharacterLiteralExpressionSyntax => TypeSymbol.Char,
			CallExpressionSyntax call when call.FunctionName == "sizeof" => TypeSymbol.Int,
			CallExpressionSyntax call => context.ResolvedCalls.TryGetValue(call, out var resolved) ? resolved.ReturnType
				: context.ResolvedDelegateCalls.TryGetValue(call, out var resolvedDelegate) ? resolvedDelegate.ReturnType
				: null,
			LambdaExpressionSyntax lam => context.ResolvedLambdas.TryGetValue(lam, out var lamInfo) ? lamInfo.Delegate : null,
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
			DefaultExpressionSyntax d => context.ResolveType(d.TypeName),
			UnaryExpressionSyntax unary => GetUnaryExpressionType(unary, scope),
			AsmExpressionSyntax asm => GetAsmExpressionType(asm, scope),
			NameofExpressionSyntax => TypeSymbol.String,
			TypeofExpressionSyntax => context.ResolveType("System.Type"),
			IsPatternExpressionSyntax => TypeSymbol.Bool,
			BinaryExpressionSyntax bin when bin.Operator is "|" or "&" or "^" => GetFlagsBinaryType(bin, scope),
			BinaryExpressionSyntax bin when bin.Operator == "+" && IsConstantStringExpression(bin.Left) && IsConstantStringExpression(bin.Right) => TypeSymbol.String,
			_ => null
		};
	}

	/// <summary>
	/// Target-types a lambda against an expected DelegateTypeSymbol (§4 contextual lambda
	/// typing): parameters bind positionally from the delegate signature, and the body is
	/// validated against the delegate's return type. Records the binding in
	/// <see cref="BindingContext.ResolvedLambdas"/> for the emitter.
	/// </summary>
	private void CheckTargetTypedLambda(LambdaExpressionSyntax lam, DelegateTypeSymbol delegateType, SymbolTable scope)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		if (lam.CaptureMode == LambdaCaptureMode.RefVar)
		{
			// 'refvar' is parsed but deliberately unsupported for lambdas (§6, §21.4):
			// there is no mutable-borrow semantics for closure environments in this increment.
			context.Diagnostics.Report(currentFileContext, lam.Span,
				"'refvar (...) =>' lambda mode is not supported: use 'move', 'ref', or default (immutable copy) capture.",
				DiagnosticIds.RefvarLambdaModeUnsupported);
			return;
		}

		if (lam.Parameters.Count != delegateType.Parameters.Count)
		{
			context.Diagnostics.Report(currentFileContext, lam.Span,
				$"Lambda has {lam.Parameters.Count} parameter(s) but delegate '{delegateType.Name}' expects {delegateType.Parameters.Count}.",
				DiagnosticIds.LambdaParameterTypeMismatch);
			return;
		}

		// Declare lambda parameters in a child scope, resolving explicit types against
		// the delegate's signature. Positional types dominate any explicit annotations.
		var lambdaScope = new SymbolTable(scope);
		for (var i = 0; i < lam.Parameters.Count; i++)
		{
			var lambdaParam = lam.Parameters[i];
			var delegateParam = delegateType.Parameters[i];

			if (lambdaParam.ExplicitType is not null)
			{
				var annotatedType = context.ResolveType(lambdaParam.ExplicitType);
				if (annotatedType is null)
				{
					context.Diagnostics.Report(currentFileContext, lambdaParam.Span,
						$"Unknown type '{lambdaParam.ExplicitType}' in lambda parameter.");
				}
				else if (!annotatedType.Equals(delegateParam.Type))
				{
					context.Diagnostics.Report(currentFileContext, lambdaParam.Span,
						$"Lambda parameter '{lambdaParam.Name}' has type '{annotatedType.Name}' but delegate '{delegateType.Name}' declares '{delegateParam.Type.Name}'.",
						DiagnosticIds.LambdaParameterTypeMismatch);
				}
			}

			lambdaScope.Declare(new VariableSymbol(lambdaParam.Name, delegateParam.Type, isMutable: false)
			{
				IsInitialized = true,
				Origin = OriginKind.Parameter,
			});
		}

		context.ResolvedLambdas[lam] = new LambdaBindingInfo
		{
			Delegate = delegateType,
			CaptureMode = lam.CaptureMode,
			ParameterTypes = [.. delegateType.Parameters.Select(p => p.Type)],
			ReturnType = delegateType.ReturnType,
			BodyIsValueExpression = lam.BodyKind == LambdaBodyKind.Expression,
		};

		if (lam.ExpressionBody is not null)
		{
			CheckExpression(lam.ExpressionBody, lambdaScope);
			var bodyType = GetExpressionType(lam.ExpressionBody, lambdaScope);
			if (bodyType != null && !bodyType.Equals(delegateType.ReturnType))
			{
				context.Diagnostics.Report(currentFileContext, lam.ExpressionBody.Span,
					$"Lambda body has type '{bodyType.Name}' but delegate '{delegateType.Name}' returns '{delegateType.ReturnType.Name}'.",
					DiagnosticIds.LambdaReturnTypeMismatch);
			}
		}
		else if (lam.BlockBody is not null)
		{
			var prevLambdaReturn = _validation.LambdaReturnType;
			_validation.LambdaReturnType = delegateType.ReturnType;
			var prevUnsafeDepth = _validation.UnsafeDepth;
			// Lambda bodies are validated as safe-callable bodies (§21.3): the enclosing
			// function's unsafe context must not leak into the lambda.
			_validation.UnsafeDepth = 0;
			try
			{
				CheckBlock(lam.BlockBody, lambdaScope, _validation.EnclosingFunction!);
			}
			finally
			{
				_validation.LambdaReturnType = prevLambdaReturn;
				_validation.UnsafeDepth = prevUnsafeDepth;
			}

			if (!delegateType.ReturnType.Equals(TypeSymbol.Void) && !EndsWithReturn(lam.BlockBody))
			{
				context.Diagnostics.Report(currentFileContext, lam.BlockBody.Span,
					$"Lambda body does not end with a return statement but delegate '{delegateType.Name}' returns '{delegateType.ReturnType.Name}'.",
					DiagnosticIds.LambdaReturnTypeMismatch);
			}
		}
	}

	/// <summary>
	/// Converts a function/method group reference (identifier, or receiver-qualified member)
	/// to a target delegate type via contextual overload resolution (§22). Records the chosen
	/// <see cref="FunctionSymbol"/> in <see cref="BindingContext.ResolvedFunctionConversions"/>.
	/// </summary>
	private void CheckFunctionGroupConversion(ExpressionSyntax groupRef, DelegateTypeSymbol delegateType, SymbolTable scope)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		var candidates = new List<FunctionSymbol>();

		switch (groupRef)
		{
			case IdentifierExpressionSyntax id:
				Overloads.GatherCandidates(id.Name, candidates);
				break;
			case MemberAccessExpressionSyntax ma when IsMethodGroupReference(ma, scope):
				{
					var receiverType = GetExpressionType(ma.Expression, scope);
					if (receiverType is PointerTypeSymbol ptr)
						receiverType = ptr.ReferencedType;
					if (receiverType is null)
						return;
					candidates.AddRange(context
						.GetExtensionMethodCandidates(receiverType, context.CurrentUnit, ma.MemberName)
						.Select(candidate => candidate.Function));
					break;
				}
		}

		var isBoundMethod = groupRef is MemberAccessExpressionSyntax;
		var matches = candidates
			.Where(f =>
			{
				if (f.IsVariadic)
					return false;
				var signatureParams = isBoundMethod && f.Parameters.Count > 0 && f.Parameters[0].Name == "this"
					? f.Parameters.Skip(1).ToList()
					: f.Parameters;
				return signatureParams.Count == delegateType.Parameters.Count &&
					   f.ReturnType.Equals(delegateType.ReturnType) &&
					   signatureParams.Zip(delegateType.Parameters, (p, d) => p.Type.Equals(d.Type)).All(match => match);
			})
			.ToList();

		if (matches.Count == 0)
		{
			context.Diagnostics.Report(currentFileContext, groupRef.Span,
				$"No function or method group named '{groupRef.ToString()}' matches delegate '{delegateType.Name}'.",
				DiagnosticIds.InvalidFunctionConversion);
			return;
		}

		if (matches.Count > 1)
		{
			var names = string.Join(", ", matches.Select(f => f.Name));
			context.Diagnostics.Report(currentFileContext, groupRef.Span,
				$"Function or method group '{groupRef.ToString()}' is ambiguous for delegate '{delegateType.Name}': {names}",
				DiagnosticIds.AmbiguousFunctionConversion);
			return;
		}

		context.ResolvedFunctionConversions[groupRef] = matches[0];
	}

	/// <summary>
	/// Checks an expression that supplies a delegate-typed value: target-typed lambdas,
	/// function/method group conversions, rejected null literals, or a plain value expression.
	/// Shared by variable declarations, struct/union member initializers, and parameter passes.
	/// </summary>
	private void CheckDelegateValueExpression(ExpressionSyntax expr, DelegateTypeSymbol delegateType, SymbolTable scope)
	{
		if (expr is LambdaExpressionSyntax targetLambda)
		{
			CheckTargetTypedLambda(targetLambda, delegateType, scope);
		}
		else if (expr is NullLiteralExpressionSyntax)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span,
				$"Cannot initialize delegate '{delegateType.Name}' with 'null'; delegates are non-null values.",
				DiagnosticIds.NullLiteralForDelegate);
		}
		else if (expr is IdentifierExpressionSyntax groupId && !Calls.IsKnownVariable(groupId, scope) && Overloads.HasCandidates(groupId.Name))
		{
			CheckFunctionGroupConversion(groupId, delegateType, scope);
		}
		else if (expr is MemberAccessExpressionSyntax groupMa && IsMethodGroupReference(groupMa, scope))
		{
			CheckFunctionGroupConversion(groupMa, delegateType, scope);
		}
		else
		{
			CheckExpression(expr, scope);
		}
	}

	/// <summary>True if the member access names a zero-arg-this extension member on the
	/// receiver's type (a bound-method group) rather than a struct/union field.</summary>
	private bool IsMethodGroupReference(MemberAccessExpressionSyntax ma, SymbolTable scope)
	{
		// Namespace-qualified globals are values, not method groups. Resolve the full
		// access before inspecting its receiver so namespace prefixes are not checked
		// as ordinary variables (for example: System.Math.Int.MaxValue).
		if (TryResolveNamespaceGlobal(ma, out _))
			return false;

		var receiverType = GetExpressionType(ma.Expression, scope);
		if (receiverType is PointerTypeSymbol ptr)
			receiverType = ptr.ReferencedType;
		if (receiverType is null)
			return false;
		if (receiverType is StructTypeSymbol structType && structType.FindField(ma.MemberName) is not null)
			return false;
		if (receiverType is UnionTypeSymbol unionType && unionType.FindField(ma.MemberName) is not null)
			return false;
		return context.GetExtensionMethodCandidates(receiverType, context.CurrentUnit, ma.MemberName).Count > 0;
	}

	private TypeSymbol? GetFlagsBinaryType(BinaryExpressionSyntax bin, SymbolTable scope)
	{
		// (§3.B) Typing for the synthesized [Flags] operators: '|', '&', '^' preserve the
		// enum type. Mixed/int operands are caught by CheckEnumIntMismatch during
		// CheckExpression; for pure-integer binaries keep the historical null result
		// (vars fall back to int) so nothing changes for non-enum code.
		var left = GetExpressionType(bin.Left, scope);
		var right = GetExpressionType(bin.Right, scope);
		return left is EnumTypeSymbol ? left : right is EnumTypeSymbol ? right : null;
	}

	private TypeSymbol? GetAsmExpressionType(AsmExpressionSyntax asm, SymbolTable scope)
	{
		if (asm.ResultType is not null)
			return context.ResolveType(asm.ResultType);

		var output = asm.Operands.FirstOrDefault(o => o.IsOutput);
		return output is not null ? GetExpressionType(output.Expression, scope) : TypeSymbol.Void;
	}

	private string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
			return id.Name;
		if (expr is MemberAccessExpressionSyntax m)
			return GetBaseIdentifierName(m.Expression);
		if (expr is BorrowExpressionSyntax b)
			return GetBaseIdentifierName(b.Expression);
		return null;
	}

	private string? ResolveFunctionTemplateName(string name, SymbolTable scope)
	{
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.GenericFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = context.GetActiveUsings(context.CurrentUnit);

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.GenericFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.GenericFunctionTemplates.ContainsKey(name))
			return name;
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

		var instSymbol = new FunctionSymbol(instName, returnType, parameters)
		{
			Visibility = templateDecl.Visibility,
			SafetyTier = templateDecl.Modifier ?? SafetyTier.Safe
		};
		var templateMangledNameForUnit = ResolveFunctionTemplateName(templateDecl.Name, scope);
		if (templateMangledNameForUnit is not null && context.SymbolUnits.TryGetValue(templateMangledNameForUnit, out var declaringUnit))
			instSymbol.DeclaringUnit = declaringUnit;
		context.MonomorphizedFunctions[instName] = instSymbol;

		var instBody = SubstituteBlockGenerics(templateDecl.Body, substitutionMap);
		var instDecl = new FunctionDeclarationSyntax(templateDecl.Span, returnType.Name, instName, [], instParameters, instBody, modifier: templateDecl.Modifier, visibility: templateDecl.Visibility);

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
			localScope.Declare(new VariableSymbol(p.Name, p.Type, p.Type is PointerTypeSymbol { IsMutable: true }) { IsInitialized = true, Origin = OriginKind.Parameter });
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
		if (context.InterfaceFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = context.GetActiveUsings(context.CurrentUnit);

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.InterfaceFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.InterfaceFunctionTemplates.ContainsKey(name))
			return name;
		return null;
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
		if (templateName is null)
			return null;

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

			if (context.ResolveType(interfaceTypeName) is not InterfaceTypeSymbol iface)
				continue;

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

			if (!Interfaces.Conforms(concrete, iface))
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

		if (substitutionMap.Count == 0)
			return null;

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

		var instSymbol = new FunctionSymbol(instName, returnType, parameters)
		{
			Visibility = templateDecl.Visibility,
			SafetyTier = templateDecl.Modifier ?? SafetyTier.Safe
		};

		if (context.SymbolUnits.TryGetValue(templateMangledName, out var declaringUnit))
			instSymbol.DeclaringUnit = declaringUnit;
		context.MonomorphizedFunctions[instName] = instSymbol;

		var instBody = SubstituteBlockGenerics(templateDecl.Body, substitutionMap);
		var instDecl = new FunctionDeclarationSyntax(templateDecl.Span, returnType.Name, instName, [], instParameters, instBody, modifier: templateDecl.Modifier, visibility: templateDecl.Visibility);

		context.MonomorphizedFunctionDecls.Add(instDecl);

		if (context.SymbolUnits.TryGetValue(templateMangledName, out var templateUnit))
		{
			context.SymbolUnits[instName] = templateUnit;
		}

		var localScope = new SymbolTable(context.Globals);
		foreach (var p in parameters)
		{
			localScope.Declare(new VariableSymbol(p.Name, p.Type, p.Type is PointerTypeSymbol { IsMutable: true }) { IsInitialized = true, Origin = OriginKind.Parameter });
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
		if (context.ProtocolFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = context.GetActiveUsings(context.CurrentUnit);

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.ProtocolFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.ProtocolFunctionTemplates.ContainsKey(name))
			return name;
		return null;
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
		if (templateName is null)
			return null;

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

			if (context.ResolveType(protocolTypeName) is not ProtocolTypeSymbol proto)
				continue;

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

			if (!Protocols.Conforms(concrete, proto))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not structurally conform to protocol '{proto.Name}' for parameter '{param.Name}'");
				return null;
			}

			// Protocol `for ...` requires-clause (lazy, at the dispatch call site):
			// the concrete type must itself satisfy the named contract. Missing or
			// unresolvable constraints are treated conservatively as non-conforming.
			if (proto.Constraint is not null && !Protocols.SatisfiesConstraint(concrete, proto.Constraint))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not satisfy the requires-clause '{proto.Constraint}' of protocol '{proto.Name}'.");
				return null;
			}

			conformedPairs.Add((concrete, proto));

			// Ambiguity rule (spec §7.C): a concrete type matching several contracts
			// (declared in different extension namespaces) for the same member
			// signature yields more than one distinct implementation -> error.
			if (Protocols.TryFindAmbiguousMember(concrete, proto, out var ambiguousMember))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Ambiguous implementation of '{ambiguousMember}' for protocol '{proto.Name}' on type '{concrete.Name}': multiple extension methods match the required signature.");
				return null;
			}

			if (substitutionMap.TryGetValue(protocolTypeName, out var existing) && existing.Name != concrete.Name)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Protocol parameter '{param.Name}' requires a single concrete type, but both '{existing.Name}' and '{concrete.Name}' were passed");
				return null;
			}

			substitutionMap[protocolTypeName] = concrete;
		}

		if (substitutionMap.Count == 0)
			return null;

		// Inherited default implementations (spec §4): materialize a substituted
		// copy of each default onto its conforming concrete type so calls inside
		// the monomorphized body resolve to a real function.
		foreach (var (conformed, conformedProto) in conformedPairs.Distinct())
			ProtocolDefaults.Materialize(conformed, conformedProto);

		return InstantiateProtocolFunction(templateDecl, substitutionMap, scope);
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
				return new WhileStatementSyntax(w.Span, SubstituteExpressionGenerics(w.Condition, substitutionMap), SubstituteStatementGenerics(w.Body, substitutionMap), w.Label);

			case ForStatementSyntax f:
				return new ForStatementSyntax(f.Span, SubstituteStatementGenerics(f.Initializer, substitutionMap) as VariableDeclarationSyntax ?? f.Initializer, SubstituteExpressionGenerics(f.Condition, substitutionMap), SubstituteExpressionGenerics(f.Increment, substitutionMap), SubstituteStatementGenerics(f.Body, substitutionMap), f.Label);

			case ForEachStatementSyntax forEach:
				{
					var newExplicitType = forEach.ExplicitItemType;
					if (newExplicitType != null)
					{
						foreach (var kv in substitutionMap)
							newExplicitType = SubstituteTypeToken(newExplicitType, kv.Key, kv.Value.Name);
					}
					return new ForEachStatementSyntax(forEach.Span, forEach.BindingKind, newExplicitType, forEach.ItemName, SubstituteExpressionGenerics(forEach.Collection, substitutionMap), SubstituteStatementGenerics(forEach.Body, substitutionMap) as BlockStatementSyntax ?? forEach.Body, forEach.Label);
				}

			case ReturnStatementSyntax r:
				return new ReturnStatementSyntax(r.Span, r.Expression != null ? SubstituteExpressionGenerics(r.Expression, substitutionMap) : null);

			case ExpressionStatementSyntax e:
				return new ExpressionStatementSyntax(e.Span, SubstituteExpressionGenerics(e.Expression, substitutionMap));

			case SwitchStatementSyntax s:
				return new SwitchStatementSyntax(s.Span, SubstituteExpressionGenerics(s.Expression, substitutionMap),
					s.Cases.Select(c => new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault,
						c.Body.Select(st => SubstituteStatementGenerics(st, substitutionMap)).ToList())).ToList());

			case SwitchCaseSyntax c:
				return new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault,
					c.Body.Select(st => SubstituteStatementGenerics(st, substitutionMap)).ToList());

			case LabeledBlockStatementSyntax lb:
				return new LabeledBlockStatementSyntax(lb.Span, lb.Label, SubstituteBlockGenerics(lb.Body, substitutionMap));

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

			case IsPatternExpressionSyntax isPat:
				return new IsPatternExpressionSyntax(isPat.Span, SubstituteExpressionGenerics(isPat.Operand, substitutionMap), isPat.VariantName, isPat.BoundName);

			case CallExpressionSyntax call:
				var newTypeArgs = call.TypeArguments.Select(t =>
				{
					var substituted = t;
					foreach (var kv in substitutionMap)
					{
						substituted = SubstituteTypeToken(substituted, kv.Key, kv.Value.Name);
					}

					return substituted;
				}).ToList();
				var newArgs = call.Arguments.Select(a => SubstituteExpressionGenerics(a, substitutionMap)).ToList();
				return new CallExpressionSyntax(call.Span, call.FunctionName, newTypeArgs, newArgs, call.ArgumentListSpan);

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

			case DefaultExpressionSyntax def:
				if (def.TypeName is null)
					return def;

				var newDefaultType = def.TypeName;
				foreach (var kv in substitutionMap)
				{
					newDefaultType = SubstituteTypeToken(newDefaultType, kv.Key, kv.Value.Name);
				}

				return new DefaultExpressionSyntax(def.Span, newDefaultType);

			default:
				return expr;
		}
	}

	private static bool EndsWithReturn(SyntaxNode s) => s switch
	{
		BlockStatementSyntax b => b.Statements.Count > 0 && EndsWithReturn(b.Statements[^1]),
		LabeledBlockStatementSyntax lb => lb.Body.Statements.Count > 0 && EndsWithReturn(lb.Body.Statements[^1]),
		ReturnStatementSyntax => true,
		TryStatementSyntax t => EndsWithReturn(t.Body) && t.CatchClauses.All(c => EndsWithReturn(c.Body)),
		_ => false,
	};


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
		if (type is not StructTypeSymbol structType)
			return;

		foreach (var init in expr.Initializers)
		{
			var field = structType.FindField(init.MemberName);
			if (field is null)
				continue;

			if (!context.LegacyVisibility && field.Visibility == Visibility.Private && !VisibilityChecker.IsAccessible(field.Visibility, context.CurrentUnit, GetDeclaringUnit(structType)))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span,
					$"Cannot initialize private field '{init.MemberName}' using an external struct literal. Use an authorized constructor within the type's defining package module boundary.", DiagnosticIds.PrivateFieldLiteralInit);
			}

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

	/// <summary>
	/// Performs semantic validation on extension method bodies (including ctors/dtors).
	/// 1. Determines mutability of 'this' (via auto-inference or explicit markers).
	/// 2. Enforces [StrictMutability] contracts if the receiver type is restricted.
	/// 3. Validates that read-only 'ref this' receivers do not perform field mutations.
	/// 4. Upgrades the function symbol's 'this' parameter mutability for downstream codegen.
	/// </summary>
	private void CheckExtensionMethodBody(string extendedTypeName, FunctionDeclarationSyntax method, bool forceMutableThis = false)
	{
		var extendedType = context.ResolveType(extendedTypeName);
		if (extendedType != null && extendedType.GetType().Name == "ProtocolTypeSymbol")
		{
			// PROTOCOL DEFAULT BODIES: validated once at declaration against a
			// this-free scope (no receiver object or flat struct fields exist).
			// A default body may only reference globals/functions and its own
			// explicit parameters.
			var baseUnsafeDepth = _validation.UnsafeDepth;
			_validation.UnsafeDepth = IsUnsafeFunction(method) ? 1 : 0;
			var baseInUnboundP = _validation.InUnbound;
			_validation.InUnbound = method.Modifier == SafetyTier.Unbound;
			var protoScope = new SymbolTable(context.Globals);
			foreach (var p in method.Parameters)
			{
				var pt = context.ResolveType(p.Type);
				if (pt != null)
				{
					protoScope.Declare(new VariableSymbol(p.Name, pt, isMutable: false) { IsInitialized = true, Origin = OriginKind.Parameter });
				}
			}

			CheckBlock(method.Body, protoScope, method);
			_validation.UnsafeDepth = baseUnsafeDepth;
			_validation.InUnbound = baseInUnboundP;
			return;
		}

		// (EnumTypeSymbol is handled via reflection-like fallback in case it's in another branch)
		if (extendedType is not (StructTypeSymbol or UnionTypeSymbol) && extendedType?.GetType().Name != "EnumTypeSymbol")
			return;

		var baseUnsafeDepth2 = _validation.UnsafeDepth;
		_validation.UnsafeDepth = IsUnsafeFunction(method) ? 1 : 0;
		var baseInUnbound2 = _validation.InUnbound;
		_validation.InUnbound = method.Modifier == SafetyTier.Unbound;

		var structType = extendedType as StructTypeSymbol;
		bool isMutating;
		var isCtorOrDtor = method.Name.Contains('~') || method.Name == extendedTypeName || method.Name.EndsWith($".{extendedTypeName}");

		// 1. StrictMutability Enforcement:
		// When [StrictMutability] is present, auto-inference is disabled.
		// Every method (excluding constructors and destructors) must explicitly declare 'ref this' or 'refvar this'.
		if (structType?.IsStrictMutability == true && !isCtorOrDtor)
		{
			if (method.Receiver == ReceiverContract.None)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan,
					$"Method '{method.Name}' must declare 'ref this' or 'refvar this' receiver in [StrictMutability] struct '{extendedTypeName}'.");
			}
		}

		// 2. Mutability Determination & Verification
		if (method.Receiver == ReceiverContract.Refvar)
		{
			isMutating = true;
		}
		else if (method.Receiver == ReceiverContract.Ref)
		{
			isMutating = false;
			// Guard: 'ref this' methods are strictly forbidden from mutating fields
			if (structType != null && DetectFieldMutation(method.Body, structType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan,
					$"Extension method '{method.Name}' declares read-only 'ref this' receiver but mutates field(s) of '{extendedTypeName}'.");
			}
		}
		else
		{
			// Fallback: Auto-inference runs only when [StrictMutability] is NOT active
			isMutating = forceMutableThis || (structType != null && DetectFieldMutation(method.Body, structType));

			// CVL1011 Warning: Notify developer when auto-inference infers mutability
			if (isMutating && !forceMutableThis && !isCtorOrDtor && structType?.IsStrictMutability != true)
			{
				// Check if this method symbol suppresses CVL1011
				var baseMangledNameForLookup = context.GetMangledName($"{extendedTypeName}.{method.Name}", context.CurrentNamespace);
				var lookupThisTypeForWarn = new PointerTypeSymbol(extendedType!, isMutable: false);
				var lookupParamsForWarn = new List<TypeSymbol> { lookupThisTypeForWarn };
				foreach (var p in method.Parameters)
					lookupParamsForWarn.Add(context.ResolveType(p.Type)!);
				var overloadedNameForWarn = context.GetOverloadedMangledName(baseMangledNameForLookup, lookupParamsForWarn);
				var targetFuncSymbol = context.Globals.Lookup(overloadedNameForWarn) as FunctionSymbol;

				if (targetFuncSymbol == null || !targetFuncSymbol.SuppressedWarnings.Contains(DiagnosticIds.AutoInferMutationWarning))
				{
					context.Diagnostics.ReportWarning(context.FileContexts[context.CurrentUnit!], method.NameSpan,
						$"Auto-inference chose mutability for method '{method.Name}'. Explicitly mark 'refvar this' to silence this warning.",
						DiagnosticIds.AutoInferMutationWarning);
				}
			}
		}

		// 3. Locate the registered function symbol using the base registration
		var baseMangledName = context.GetMangledName($"{extendedTypeName}.{method.Name}", context.CurrentNamespace);
		var lookupThisType = new PointerTypeSymbol(extendedType!, isMutable: false);
		var lookupParams = new List<TypeSymbol> { lookupThisType };
		foreach (var p in method.Parameters)
		{
			lookupParams.Add(context.ResolveType(p.Type)!);
		}

		var lookupOverloadedName = context.GetOverloadedMangledName(baseMangledName, lookupParams);
		var funcSymbol = context.Globals.Lookup(lookupOverloadedName) as FunctionSymbol;

		if (funcSymbol is not null)
		{
			// Upgrade the registered symbol's "this" parameter mutability
			if (funcSymbol.Parameters[0].Type is PointerTypeSymbol thisParamType)
			{
				thisParamType.IsMutable = isMutating;
			}
		}

		// 4. Populate local scope with fields/variants so they can be written as flat local variables
		var localScope = new SymbolTable(context.Globals);

		// EXPLICITLY DECLARE 'this' in the local scope!
		var thisPtrType = new PointerTypeSymbol(extendedType!, isMutable: isMutating);
		localScope.Declare(new VariableSymbol("this", thisPtrType, isMutable: false) { IsInitialized = true });

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
		else if (extendedType?.GetType().Name == "EnumTypeSymbol")
		{
			// Restore the E7 logic: make enum variants accessible unqualified
			dynamic enumType = extendedType;
			foreach (var variant in enumType.Variants)
			{
				localScope.Declare(new VariableSymbol((string)variant.Name, extendedType, isMutable: false) { IsInitialized = true });
			}
		}

		foreach (var param in method.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is not null)
			{
				localScope.Declare(new VariableSymbol(param.Name, paramType, isMutable: false) { IsInitialized = true, Origin = OriginKind.Parameter });
			}
		}

		CheckBlock(method.Body, localScope, method);

		// Restore original depth context
		_validation.UnsafeDepth = baseUnsafeDepth2;
		_validation.InUnbound = baseInUnbound2;
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
		if (exprType is null)
			return;

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

		_validation.SwitchDepth++;
		try
		{
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

				if (!context.LegacyVisibility && c.VariableName is not null && !VisibilityChecker.IsAccessible(variant.Visibility, context.CurrentUnit, GetDeclaringUnit(unionType)))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, c.Span,
						$"Pattern matching binding failed for type '{unionType.Name}'. Payload variant field '{variant.Name}' is obscured by visibility constraints.", DiagnosticIds.HiddenPayloadMatch);
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
		}
		finally
		{
			_validation.SwitchDepth--;
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

		_validation.SwitchDepth++;
		try
		{
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
		}
		finally
		{
			_validation.SwitchDepth--;
		}

		// (§6.A) 'Exhaustive Switch-Matching check'. [Flags] enums are RELAXED: composite
		// masks make total coverage impossible, so a flags switch never demands a case
		// for every variant (a default remains optional; the trap fallback guards the rest).
		// [NonExhaustive] (§6.C) keeps its exhaustive contract in the DEFINING unit; only a
		// consumer in a different unit (no cross-package model, so 'unit' is the file group)
		// is exempt - it must instead synthesize a developer default below.
		var isExternalConsumer = enumType.IsNonExhaustive
			&& context.SymbolUnits.TryGetValue(enumType.Name, out var declaringUnit)
			&& !ReferenceEquals(declaringUnit, context.CurrentUnit);

		if (!hasDefault && !enumType.IsFlags && !isExternalConsumer)
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

		// (§6.C) Consumer side: unhandled runtime values must flow to a developer-written
		// default / case _, never to the implicit default: llvm.trap fallback.
		if (isExternalConsumer && !hasDefault)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, sw.Span,
				$"[NonExhaustive] enum '{enumType.Name}' is consumed from another unit and requires an explicit 'default' or 'case _' branch.");
		}

		// (§6.A) Non-Void Return Verification: a [NonExhaustive] switch in a non-void
		// function must be able to leave a value behind - its default block must terminate
		// (return or a diverging call). When the switch is NOT the last statement of the
		// body, the syntactic trailing-return guard separately guarantees a downstream
		// return exists, so only the body-terminal case needs this check.
		if (enumType.IsNonExhaustive && currentFunc.ReturnType != "void" && hasDefault
			&& currentFunc.Body is BlockStatementSyntax fnBody && fnBody.Statements.Count > 0
			&& ReferenceEquals(fnBody.Statements[^1], sw))
		{
			var defaultCase = sw.Cases.First(c => c.IsDefault || c.VariantName == "_");
			if (!EndsWithDivergingStatement(defaultCase.Body))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, defaultCase.Span,
					$"The 'default' branch of a switch over [NonExhaustive] enum '{enumType.Name}' must terminate with a 'return' but ends in non-terminating statement(s).");
			}
		}
	}

	/// <summary>A statement list that can leave a non-void function: ends in a return, or a diverging call such as exit()/panic().</summary>
	private static bool EndsWithDivergingStatement(IReadOnlyList<SyntaxNode> body) => body.Count > 0 && body[^1] switch
	{
		ReturnStatementSyntax => true,
		ExpressionStatementSyntax { Expression: CallExpressionSyntax call } when call.FunctionName == "exit" || call.FunctionName == "panic" => true,
		_ => false,
	};

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
		if (left is null || right is null)
			return;

		var leftIsEnum = left is EnumTypeSymbol;
		var rightIsEnum = right is EnumTypeSymbol;
		if (leftIsEnum == rightIsEnum)
			return;

		var enumType = leftIsEnum ? left : right;
		var otherType = leftIsEnum ? right : left;
		if (!TypeSymbol.IsIntegerType(otherType))
			return;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, span,
			$"Implicit conversion between enum '{enumType.Name}' and '{otherType.Name}' is forbidden; use an explicit cast.");
	}

	private static bool IsConstantStringExpression(ExpressionSyntax expr)
	{
		return expr switch
		{
			StringLiteralExpressionSyntax => true,
			BinaryExpressionSyntax bin when bin.Operator == "+" =>
				IsConstantStringExpression(bin.Left) && IsConstantStringExpression(bin.Right),
			_ => false,
		};
	}

	private static string? GetNameofFoldedName(ExpressionSyntax expr) => expr switch
	{
		IdentifierExpressionSyntax id => id.Name,
		MemberAccessExpressionSyntax m => m.MemberName,
		_ => null,
	};

	private void CheckNameofExpression(NameofExpressionSyntax nameofExpr, SymbolTable scope)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		var argument = nameofExpr.Argument;

		if (GetNameofFoldedName(argument) is null)
		{
			context.Diagnostics.Report(currentFileContext, nameofExpr.Span, "Operator `nameof` cannot be applied to an expression with an empty identifier node.", DiagnosticIds.NameofExpressionInvalid);
			return;
		}

		// Static/type receiver: `nameof(StructName.Field)` must not bind the base as a variable.
		if (argument is MemberAccessExpressionSyntax mem
			&& GetBaseIdentifierName(mem.Expression) is { } baseName
			&& scope.Lookup(baseName) is not VariableSymbol
			&& context.ResolveType(baseName) is { } staticType)
		{
			if (!TryValidateStaticNameof(staticType, mem))
			{
				context.Diagnostics.Report(currentFileContext, mem.Span, $"The name {mem.MemberName} does not exist in the current context. Cannot evaluate `nameof`.", DiagnosticIds.NameofInvalidSymbolError);
			}
			return;
		}

		// Bare type name (e.g. `nameof(Point)`): nothing needs instance binding.
		var isBareTypeName = argument is IdentifierExpressionSyntax bareId
			&& scope.Lookup(bareId.Name) is not VariableSymbol
			&& context.ResolveType(bareId.Name) is not null;
		if (!isBareTypeName)
			CheckExpression(argument, scope);

		if (argument is MemberAccessExpressionSyntax memAccess && GetExpressionType(memAccess, scope) is null)
		{
			context.Diagnostics.Report(currentFileContext, memAccess.Span, $"The name {memAccess.MemberName} does not exist in the current context. Cannot evaluate `nameof`.", DiagnosticIds.NameofInvalidSymbolError);
		}
		else if (argument is IdentifierExpressionSyntax id
			&& scope.Lookup(id.Name) is not VariableSymbol
			&& context.ResolveType(id.Name) is null)
		{
			context.Diagnostics.Report(currentFileContext, id.Span, $"The name {id.Name} does not exist in the current context. Cannot evaluate `nameof`.", DiagnosticIds.NameofInvalidSymbolError);
		}
	}

	private void CheckTypeofExpression(TypeofExpressionSyntax typeofExpr, SymbolTable scope)
	{
		if (context.ResolveType(typeofExpr.TypeName) is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, typeofExpr.Span, $"Type {typeofExpr.TypeName} could not be found. Cannot evaluate `typeof`.", DiagnosticIds.TypeofInvalidTypeError);
		}
	}

	private static bool TryValidateStaticNameof(TypeSymbol type, MemberAccessExpressionSyntax mem)
	{
		var segments = new List<string>();
		ExpressionSyntax current = mem;
		while (current is MemberAccessExpressionSyntax m)
		{
			segments.Insert(0, m.MemberName);
			current = m.Expression;
		}
		if (current is not IdentifierExpressionSyntax)
			return false;

		var currentType = type;
		foreach (var segment in segments)
		{
			currentType = currentType switch
			{
				StructTypeSymbol s => s.FindField(segment)?.Type,
				UnionTypeSymbol u => u.FindField(segment)?.Type,
				EnumTypeSymbol e => e.FindVariant(segment) is null ? null : TypeSymbol.Int,
				_ => null,
			};
			if (currentType is null)
				return false;
		}
		return true;
	}

	private void CheckAsmExpression(AsmExpressionSyntax asm, SymbolTable scope)
	{
		if (_validation.UnsafeDepth == 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, asm.Span,
				"`asm` can only be used inside `unsafe` contexts.", DiagnosticIds.AsmOutsideUnsafeContext);
		}

		var outputs = asm.Operands.Where(o => o.IsOutput).ToList();
		if (asm.ResultType is not null && outputs.Count != 1)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, asm.Span,
				"`asm` with result type requires exactly one output operand.", DiagnosticIds.AsmResultRequiresOneOutput);
		}

		foreach (var operand in asm.Operands)
		{
			CheckExpression(operand.Expression, scope);

			if (operand.IsOutput && !IsAssignableLValue(operand.Expression, scope))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, operand.Span,
					"Output operand must be an l-value (assignable).", DiagnosticIds.AsmOutputNotLValue);
			}

			if (!IsValidConstraint(operand.Constraint))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, operand.Span,
					$"Invalid constraint `{operand.Constraint}`.", DiagnosticIds.InvalidAsmConstraint);
			}
			else if (TryGetFixedRegister(operand.Constraint) is { } fixedReg)
			{
				var operandType = GetExpressionType(operand.Expression, scope);
				if (operandType is not null && !IsAsmRegistrable(operandType))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, operand.Span,
						$"Type mismatch for operand `{operand.Name ?? operand.Constraint}`.", DiagnosticIds.AsmOperandTypeMismatch);
				}
			}
		}

		foreach (var clobber in asm.Clobbers)
		{
			if (!ValidClobberRegisters.Contains(clobber))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, asm.Span,
					$"Invalid clobber register `{clobber}`.", DiagnosticIds.InvalidAsmClobber);
			}
		}
	}

	private bool IsAssignableLValue(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				return scope.Lookup(id.Name) is VariableSymbol
					|| context.ResolveGlobalReference(id.Name, out _) is not null;
			case MemberAccessExpressionSyntax or IndexExpressionSyntax:
				return true;
			case UnaryExpressionSyntax { Operator: "*" }:
				return true;
			default:
				return false;
		}
	}

	private static bool IsAsmRegistrable(TypeSymbol t)
	{
		return t is RawPointerTypeSymbol or SliceTypeSymbol
			|| TypeSymbol.IsNumericIntegerType(t)
			|| TypeSymbol.IsFloatingPointType(t)
			|| t.Equals(TypeSymbol.Bool) || t.Equals(TypeSymbol.Char);
	}

	private static bool IsValidConstraint(string constraint)
	{
		if (string.IsNullOrWhiteSpace(constraint))
			return false;

		var body = constraint.TrimStart('=', '+', '&', '%');
		if (body.StartsWith('{'))
			return body.EndsWith('}') && body.Length > 2 && ValidClobberRegisters.Contains(body[1..^1]);

		return body.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ',' or '.' or '!');
	}

	private static string? TryGetFixedRegister(string constraint)
	{
		var body = constraint.TrimStart('=', '+', '&', '%');
		if (body.StartsWith('{') && body.EndsWith('}') && body.Length > 2)
			return body[1..^1];

		return null;
	}

	private static readonly HashSet<string> ValidClobberRegisters = new(StringComparer.Ordinal)
	{
		// x86_64 GPRs (r15 and its sub-registers are intentionally excluded: the
		// inline-assembly spec corpus treats "r15" as an invalid clobber register).
		"rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp",
		"r8", "r9", "r10", "r11", "r12", "r13", "r14",
		"eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp",
		"r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d",
		"ax", "bx", "cx", "dx", "si", "di", "bp", "sp",
		"r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w",
		"al", "bl", "cl", "dl", "sil", "dil", "bpl", "spl",
		"r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b",
		"xmm0", "xmm1", "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7",
		"xmm8", "xmm9", "xmm10", "xmm11", "xmm12", "xmm13", "xmm14", "xmm15",
		"mm0", "mm1", "mm2", "mm3", "mm4", "mm5", "mm6", "mm7",
		"st0", "st1", "st2", "st3", "st4", "st5", "st6", "st7",
		"flags", "eflags", "memory", "cc", "dirflag", "fpcw", "fpsw", "fpcr",
	};

	private void CheckUnaryCast(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		if (!unary.Operator.StartsWith('(') || !unary.Operator.EndsWith("*)") || unary.Operator.Length < 4)
			return;

		var operandType = GetExpressionType(unary.Operand, scope);
		if (operandType is null)
			return;

		if (operandType is UnionTypeSymbol optionUnion && optionUnion.IsNpoEligible)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, unary.Span,
				$"Cannot cast nullable reference option '{optionUnion.Name}' directly to a raw pointer; pattern-match it (switch on 'ref'/'refvar') to extract a non-null reference first.");
			return;
		}

		// Destructive cast '(T*)x' extracts the owning heap pointer from a heap-allocated
		// handle. A plain stack value has no hidden pointer to extract, so reject it here
		// (function parameters are allowed: they may already carry a handle by value).
		var targetTypeName = unary.Operator.Substring(1, unary.Operator.Length - 3);
		var targetType = context.ResolveType(targetTypeName);

		if (targetType is not null && targetType.Equals(operandType))
		{
			if (unary.Operand is IdentifierExpressionSyntax id)
			{
				var sym = scope.Lookup(id.Name) as VariableSymbol;
				if (sym is not null && !sym.IsHeapAllocated && sym.Origin != OriginKind.Parameter)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, unary.Span,
						$"Destructive cast '({targetTypeName}*)' requires an owning heap handle; '{id.Name}' is a stack value. Allocate it with 'heap {targetTypeName} {{ ... }}' or 'heap {targetTypeName}(...)', or cast its address with '&{id.Name}'.");
				}
			}
		}

		if (targetType is not null && targetType.Equals(TypeSymbol.Char) &&
			operandType.Equals(TypeSymbol.String) && _validation.UnsafeDepth == 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, unary.Span,
				"Casting a `string` to `char*` requires an unsafe context. Wrap the cast in `unsafe { }` or mark the enclosing function `[UnsafeBody]`.",
				DiagnosticIds.StringToCharPointerOutsideUnsafe);
		}
	}

	private void CheckIsPatternExpression(IsPatternExpressionSyntax isPat, SymbolTable scope)
	{
		CheckExpression(isPat.Operand, scope);
		var operandType = GetExpressionType(isPat.Operand, scope);
		if (operandType is null)
			return;

		if (operandType is PointerTypeSymbol ptr)
			operandType = ptr.ReferencedType;

		if (operandType is not UnionTypeSymbol unionType)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, isPat.Span, $"The 'is' pattern can only be applied to a union type, got '{operandType.Name}'.");
			return;
		}

		var variant = unionType.FindField(isPat.VariantName);
		if (variant is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, isPat.Span, $"Union '{unionType.Name}' does not contain variant '{isPat.VariantName}'");
			return;
		}

		// NPO options (Option<ref T>) carry the stored reference flat, so the match test is a
		// single null-check and the bound value is the payload pointer; tagged unions (Option<T>
		// and general unions such as Result<T, E>) compare the tag and bind the payload value (or
		// a reference to it when the operand is a borrow).
		if (isPat.BoundName is not null)
		{
			if (variant.IsVoidVariant)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, isPat.Span, $"Void variant '{isPat.VariantName}' cannot carry a bound variable.");
				return;
			}

			// NPO option: the payload is the stored reference/pointer itself.
			// Tagged option: the payload is the value (bound by value), or a reference to the
			// payload slot when the operand was taken by borrow ('ref opt is Some v').
			var isBorrowOperand = isPat.Operand is BorrowExpressionSyntax;
			TypeSymbol promotedType = unionType.IsNpoEligible
				? variant.Type is PointerTypeSymbol inner
					? new PointerTypeSymbol(inner.ReferencedType, isMutable: inner.IsMutable)
					: variant.Type
				: isBorrowOperand
					? new PointerTypeSymbol(variant.Type, isMutable: true)
					: variant.Type;

			scope.Declare(new VariableSymbol(isPat.BoundName, promotedType, isMutable: true) { IsInitialized = true });
		}
	}

	private void CheckUnaryEnumTilde(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		// (§3.B) `~` is only meaningful for [Flags] enums, where it is the masked bitwise
		// complement (~v & CombinedAtomicMask).
		if (unary.Operator != "~")
			return;
		if (GetExpressionType(unary.Operand, scope) is not EnumTypeSymbol enumType)
			return;
		if (enumType.IsFlags)
			return;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, unary.Span,
			$"Operator '~' cannot be applied to non-[Flags] enum '{enumType.Name}'.");
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

		if (unary.Operator.Length >= 3 && unary.Operator.StartsWith('(') && unary.Operator.EndsWith(')'))
		{
			var result = context.ResolveType(unary.Operator[1..^1]);
			if (result is EnumTypeSymbol castEnum && _validation.UnsafeDepth == 0)
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

		// (§3.B) `~` on a [Flags] enum yields the enum again (inverted bits, masked at codegen).
		if (unary.Operator == "~")
		{
			if (GetExpressionType(unary.Operand, scope) is EnumTypeSymbol enumType)
				return enumType;
		}

		return GetExpressionType(unary.Operand, scope);
	}

	/// <summary>
	/// Emits CVL1012 warning if a function or type marked '[MustUse]' is called as an unused standalone statement.
	/// </summary>
	private void CheckMustUseDiscard(ExpressionSyntax expr, SymbolTable scope)
	{
		if (expr is not CallExpressionSyntax call)
			return;

		if (!context.ResolvedCalls.TryGetValue(call, out var func))
			return;

		// 1. Check if the function itself is [MustUse]
		var isFuncMustUse = func.IsMustUse;
		var funcMessage = func.MustUseMessage;

		// 2. Check if the returned type is [MustUse]
		var retType = func.ReturnType;
		if (retType is PointerTypeSymbol ptr)
			retType = ptr.ReferencedType;

		var isTypeMustUse = false;
		string? typeMessage = null;

		if (retType is StructTypeSymbol st && st.IsMustUse)
		{
			isTypeMustUse = true;
			typeMessage = st.MustUseMessage;
		}
		else if (retType is UnionTypeSymbol ut && ut.IsMustUse)
		{
			isTypeMustUse = true;
			typeMessage = ut.MustUseMessage;
		}
		else if (retType is EnumTypeSymbol et && et.IsMustUse)
		{
			isTypeMustUse = true;
			typeMessage = et.MustUseMessage;
		}

		if (isFuncMustUse || isTypeMustUse)
		{
			var reason = funcMessage ?? typeMessage;
			var msg = reason is not null
				? $"Return value of '{call.FunctionName}' is marked '[MustUse]' and must not be ignored: {reason}"
				: $"Return value of '{call.FunctionName}' is marked '[MustUse]' and must not be ignored.";

			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.ReportWarning(currentFileContext, call.Span, msg, DiagnosticIds.MustUseIgnoredWarning);
		}
	}

	/// <summary>
	/// Emits CVL1038 error if a public symbol exposes a generic type instantiation
	/// whose type argument is private or internal.
	/// </summary>
	private void CheckGenericVisibilityLeak(TextSpan span, TypeSymbol? type, string hostName)
	{
		if (type is null)
			return;

		if (type is PointerTypeSymbol ptr)
			type = ptr.ReferencedType;

		if (!type.Name.Contains('<'))
			return;

		var openBracket = type.Name.IndexOf('<');
		var closeBracket = type.Name.LastIndexOf('>');
		if (openBracket <= 0 || closeBracket <= openBracket)
			return;

		var argsPart = type.Name.Substring(openBracket + 1, closeBracket - openBracket - 1);
		foreach (var rawArg in argsPart.Split(','))
		{
			var argType = context.ResolveType(rawArg.Trim());
			if (argType is null)
				continue;

			if (argType is StructTypeSymbol st && st.Visibility < Visibility.Public)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					span,
					$"The visibility of generic type instantiation '{type.Name}' exceeds the visibility of its type argument '{st.Name}'. Upgrade the argument visibility or restrict the parent declaration.",
					DiagnosticIds.GenericVisibilityLeak);
			}
			else if (argType is UnionTypeSymbol ut && ut.Visibility < Visibility.Public)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					span,
					$"The visibility of generic type instantiation '{type.Name}' exceeds the visibility of its type argument '{ut.Name}'. Upgrade the argument visibility or restrict the parent declaration.",
					DiagnosticIds.GenericVisibilityLeak);
			}
			else if (argType is EnumTypeSymbol et && et.Visibility < Visibility.Public)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					span,
					$"The visibility of generic type instantiation '{type.Name}' exceeds the visibility of its type argument '{et.Name}'. Upgrade the argument visibility or restrict the parent declaration.",
					DiagnosticIds.GenericVisibilityLeak);
			}
		}
	}

	private static bool IsFunctionUnsafeBodyOnly(FunctionSymbol func)
	{
		// [UnsafeBody] encapsulates unsafe code safely, so callers in safe code CAN call it.
		// Pure 'unsafe fn' signatures force callers to be in an unsafe context.
		return func.IsUnsafeBody;
	}
}
