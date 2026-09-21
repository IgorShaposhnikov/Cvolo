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

	private ExpressionValidator? _expressionValidator;
	/// <summary>Shared expression semantic validator used by the single validation traversal.</summary>
	private ExpressionValidator Expressions => _expressionValidator ??= new ExpressionValidator(
		context,
		_validation,
		Classification,
		Overloads,
		Calls,
		CheckBlock,
		ResolveFunctionTemplateName,
		InstantiateGenericFunction,
		ResolveInterfaceFunctionTemplateName,
		TryResolveInterfaceCall,
		ResolveProtocolFunctionTemplateName,
		TryResolveProtocolCall,
		EndsWithReturn);

	private InterfaceConformance? _interfaceConformance;
	/// <summary>Shared nominal interface-conformance service.</summary>
	private InterfaceConformance Interfaces => _interfaceConformance ??= new InterfaceConformance(context);
	private ProtocolConformance? _protocolConformance;
	/// <summary>Shared structural protocol-conformance and ambiguity service.</summary>
	private ProtocolConformance Protocols => _protocolConformance ??= new ProtocolConformance(context, Interfaces);
	private ProtocolDefaultMaterializer? _protocolDefaults;
	/// <summary>Shared materializer for inherited protocol default implementations.</summary>
	private ProtocolDefaultMaterializer ProtocolDefaults => _protocolDefaults ??= new ProtocolDefaultMaterializer(context);

	private ConstructorValidator? _constructorValidator;
	/// <summary>Constructor-specific validation service used by the single validation traversal.</summary>
	private ConstructorValidator Constructors => _constructorValidator ??= new ConstructorValidator(
		context,
		_validation,
		Overloads,
		(extendedTypeName, method, forceMutableThis) => CheckExtensionMethodBody(extendedTypeName, method, forceMutableThis),
		CheckExpression,
		GetExpressionType);

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
						Constructors.ValidateBody(extDecl.ExtendedTypeName, ctorDecl);
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
					Constructors.ValidateBody(extendedTypeName, ctor);
				}
			}
		}
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










	/// <summary>Validates one expression through the shared expression semantic service.</summary>
	private void CheckExpression(ExpressionSyntax expr, SymbolTable scope) => Expressions.Check(expr, scope);

	/// <summary>Returns the semantic type of an expression through the shared expression semantic service.</summary>
	private TypeSymbol? GetExpressionType(ExpressionSyntax expr, SymbolTable scope) => Expressions.GetType(expr, scope);

	/// <summary>Validates a value supplied to a delegate-typed target.</summary>
	private void CheckDelegateValueExpression(ExpressionSyntax expr, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckDelegateValue(expr, delegateType, scope);

	/// <summary>Validates a lambda against an expected delegate type through the shared expression validator.</summary>
	private void CheckTargetTypedLambda(LambdaExpressionSyntax lambda, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckTargetTypedLambda(lambda, delegateType, scope);

	/// <summary>Validates a function or bound-method group conversion through the shared expression validator.</summary>
	private void CheckFunctionGroupConversion(ExpressionSyntax expression, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckFunctionGroupConversion(expression, delegateType, scope);

	/// <summary>Returns whether a member access denotes a bound method group rather than data access.</summary>
	private bool IsMethodGroupReference(MemberAccessExpressionSyntax memberAccess, SymbolTable scope)
		=> Expressions.IsMethodGroupReference(memberAccess, scope);

	/// <summary>Returns the root identifier of a nested member/borrow expression when one exists.</summary>
	private string? GetBaseIdentifierName(ExpressionSyntax expr) => Expressions.GetBaseIdentifierName(expr);

	/// <summary>
	/// Resolves the compilation unit that declared a semantic type, falling back from a concrete
	/// generic instantiation name to its template name when necessary.
	/// </summary>
	private CompilationUnitSyntax? GetDeclaringUnit(TypeSymbol type)
	{
		foreach (var name in ExpandTemplateNames(type))
		{
			if (context.SymbolUnits.TryGetValue(name, out var unit))
				return unit;
		}

		return null;
	}

	/// <summary>
	/// Enumerates the concrete semantic type name and, for generic instances, the corresponding
	/// unspecialized template name used by declaration-unit lookup.
	/// </summary>
	private static IEnumerable<string> ExpandTemplateNames(TypeSymbol type)
	{
		yield return type.Name;
		var name = type.Name;
		var lt = name.IndexOf('<');
		if (lt > 0)
			yield return name[..lt];
	}

	/// <summary>Checks that discarding an expression result does not violate must-use semantics.</summary>
	private void CheckMustUseDiscard(ExpressionSyntax expr, SymbolTable scope) => Expressions.CheckMustUseDiscard(expr, scope);

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
