using Cvolo.Analysis.Resolution;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Analysis.VisibilityChecks;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Validates statement-level semantics during the single validation traversal.
/// </summary>
/// <remarks>
/// This validator owns lexical blocks, local declarations, returns, loops, foreach validation,
/// unsafe-block state, and control-exit checks. Expression semantics and switch-specific semantics
/// are delegated to their dedicated validators so validation still performs one AST walk.
/// </remarks>
internal sealed class StatementValidator(
	BindingContext context,
	ValidationContext validation,
	ClassificationAnalyzer classification,
	OverloadResolver overloads,
	CallResolver calls,
	Action<ExpressionSyntax, SymbolTable> validateExpression,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> getExpressionType,
	Action<LambdaExpressionSyntax, DelegateTypeSymbol, SymbolTable> validateTargetTypedLambda,
	Action<ExpressionSyntax, DelegateTypeSymbol, SymbolTable> validateFunctionGroupConversion,
	Action<ExpressionSyntax, DelegateTypeSymbol, SymbolTable> validateDelegateValue,
	Func<MemberAccessExpressionSyntax, SymbolTable, bool> isMethodGroupReference,
	Action<ExpressionSyntax, SymbolTable> validateMustUseDiscard,
	Func<SwitchValidator> getSwitchValidator)
{
	private ValidationContext _validation => validation;
	private ClassificationAnalyzer Classification => classification;
	private OverloadResolver Overloads => overloads;
	private CallResolver Calls => calls;
	private SwitchValidator Switches => getSwitchValidator();

	/// <summary>Delegates expression validation to the shared expression validator.</summary>
	private void CheckExpression(ExpressionSyntax expression, SymbolTable scope) => validateExpression(expression, scope);

	/// <summary>Delegates semantic expression typing to the shared expression validator.</summary>
	private TypeSymbol? GetExpressionType(ExpressionSyntax expression, SymbolTable scope) => getExpressionType(expression, scope);

	/// <summary>Delegates target-typed lambda validation to the shared expression validator.</summary>
	private void CheckTargetTypedLambda(LambdaExpressionSyntax lambda, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> validateTargetTypedLambda(lambda, delegateType, scope);

	/// <summary>Delegates function-group conversion validation to the shared expression validator.</summary>
	private void CheckFunctionGroupConversion(ExpressionSyntax expression, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> validateFunctionGroupConversion(expression, delegateType, scope);

	/// <summary>Returns whether a member access is a bound method group rather than data access.</summary>
	private bool IsMethodGroupReference(MemberAccessExpressionSyntax memberAccess, SymbolTable scope)
		=> isMethodGroupReference(memberAccess, scope);

	/// <summary>Delegates must-use discard validation to the shared expression validator.</summary>
	private void CheckMustUseDiscard(ExpressionSyntax expression, SymbolTable scope)
		=> validateMustUseDiscard(expression, scope);

	/// <summary>Delegates contextual delegate-value validation to the shared expression validator.</summary>
	private void CheckDelegateValue(ExpressionSyntax expression, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> validateDelegateValue(expression, delegateType, scope);

	/// <summary>
	/// Validates a lexical statement block while maintaining its label scope.
	/// </summary>
	public void CheckBlock(BlockStatementSyntax? block, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
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

	/// <summary>
	/// Dispatches validation for one statement without starting a second AST traversal.
	/// </summary>
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
				Switches.Validate(sw, scope, currentFunc);
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

	/// <summary>
	/// Enters a loop validation frame and registers its optional label.
	/// </summary>
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

	/// <summary>
	/// Leaves the innermost loop validation frame.
	/// </summary>
	private void ExitLoop()
	{
		if (_validation.LoopLabels.Count > 0)
			_validation.LoopLabels.Pop();
	}

	/// <summary>
	/// Reports a statement-level diagnostic against the current compilation unit.
	/// </summary>
	private void ReportDiagnostics(TextSpan span, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, span, message, diagnosticId);
	}

	/// <summary>
	/// Validates foreach routing, iterator contracts, item binding shape, and item mutability.
	/// </summary>
	private void CheckForEachStatement(ForEachStatementSyntax forEach, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		CheckExpression(forEach.Collection, scope);
		var collectionType = GetExpressionType(forEach.Collection, scope);

		if (collectionType is null)
			return;

		var underlyingType = collectionType;
		if (underlyingType is PointerTypeSymbol ptr)
			underlyingType = ptr.ReferencedType;

		TypeSymbol? itemType;
		var currentReturnsRef = false;

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
			var isVar = forEach.BindingKind == ForEachVariableKind.Var;
			var isRefVar = forEach.BindingKind == ForEachVariableKind.RefVar;

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

	/// <summary>
	/// Resolves the visible structural <c>GetEnumerator</c> overload used by foreach validation.
	/// </summary>
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

	/// <summary>
	/// Validates break/continue targets against the active loop, block, and switch frames.
	/// </summary>
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

	/// <summary>
	/// Validates a local declaration and records its semantic variable symbol.
	/// </summary>
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
				// Safe delegates are non-null values. Native delegates are scalar-like nullable
				// function pointers and follow the ordinary local definite-assignment rules.
				if (!declaredDelegateType.IsNative)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, varDecl.Span,
						$"Delegate '{declaredDelegateType.Name}' requires an initializer.",
						DiagnosticIds.DelegateNotDefaultInitializable);
				}
			}
			else
			{
				CheckDelegateValue(varDecl.Initializer, declaredDelegateType, scope);
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

	/// <summary>
	/// Computes the existing conservative stack-size estimate used by the local-array guard.
	/// </summary>
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

	/// <summary>
	/// Validates a return expression against the current function or lambda return type.
	/// </summary>
	private void CheckReturnStatement(ReturnStatementSyntax ret, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		if (ret.Expression is null)
			return;

		var expectedType = _validation.LambdaReturnType ?? context.ResolveType(currentFunc.ReturnType);

		// A lambda (or function group) returned from a delegate-typed function is
		// target-typed against the expected delegate; skip the generic expression check.
		if (expectedType is DelegateTypeSymbol expectedDelegate)
		{
			var isTargetTypedDelegateValue = ret.Expression is LambdaExpressionSyntax
				|| (ret.Expression is IdentifierExpressionSyntax retId && !Calls.IsKnownVariable(retId, scope) && Overloads.HasCandidates(retId.Name))
				|| (ret.Expression is MemberAccessExpressionSyntax retMa && IsMethodGroupReference(retMa, scope))
				|| ret.Expression is UnaryExpressionSyntax { Operator: "&" }
				|| ret.Expression is NullLiteralExpressionSyntax;
			if (isTargetTypedDelegateValue)
			{
				CheckDelegateValue(ret.Expression, expectedDelegate, scope);
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
}
