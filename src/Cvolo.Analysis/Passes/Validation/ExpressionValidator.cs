using Cvolo.Analysis.Resolution;
using Cvolo.Analysis.Semantics;
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
/// Validates expression-level semantics during the single validation traversal.
/// </summary>
/// <remarks>
/// The validator owns expression dispatch, expression typing, aggregate and borrow checks, target-typed
/// delegate values, casts, and expression-specific diagnostics. Inline assembly and compiler intrinsics are
/// delegated to dedicated validators. Generic/interface/protocol
/// monomorphization and block traversal remain orchestration responsibilities supplied through callbacks so
/// this service does not create a second AST pass or depend on <see cref="ValidationPass"/>.
/// </remarks>
internal sealed class ExpressionValidator(
	BindingContext context,
	ValidationContext validation,
	ClassificationAnalyzer classification,
	OverloadResolver overloads,
	CallResolver calls,
	InlineAsmValidator inlineAsm,
	IntrinsicValidator intrinsics,
	Action<BlockStatementSyntax?, SymbolTable, FunctionDeclarationSyntax> validateBlock,
	Func<string, SymbolTable, string?> resolveFunctionTemplateName,
	Func<FunctionDeclarationSyntax, List<TypeSymbol>, SymbolTable, FunctionSymbol> instantiateGenericFunction,
	Func<string, SymbolTable, string?> resolveInterfaceFunctionTemplateName,
	Func<CallExpressionSyntax, IReadOnlyList<TypeSymbol>, SymbolTable, FunctionSymbol?> tryResolveInterfaceCall,
	Func<string, SymbolTable, string?> resolveProtocolFunctionTemplateName,
	Func<CallExpressionSyntax, IReadOnlyList<TypeSymbol>, SymbolTable, FunctionSymbol?> tryResolveProtocolCall,
	Func<SyntaxNode, bool> endsWithReturn)
{
	private readonly ValidationContext _validation = validation;
	private ClassificationAnalyzer Classification => classification;
	private OverloadResolver Overloads => overloads;
	private CallResolver Calls => calls;
	private InlineAsmValidator InlineAsm => inlineAsm;
	private IntrinsicValidator Intrinsics => intrinsics;

	/// <summary>Delegates lambda block validation back to the single validation traversal.</summary>
	private void CheckBlock(BlockStatementSyntax? block, SymbolTable scope, FunctionDeclarationSyntax currentFunction)
		=> validateBlock(block, scope, currentFunction);

	/// <summary>Resolves an explicit generic function template name through the orchestration layer.</summary>
	private string? ResolveFunctionTemplateName(string name, SymbolTable scope) => resolveFunctionTemplateName(name, scope);

	/// <summary>Instantiates an explicit generic function through the existing validation pipeline.</summary>
	private FunctionSymbol InstantiateGenericFunction(FunctionDeclarationSyntax template, List<TypeSymbol> typeArguments, SymbolTable scope)
		=> instantiateGenericFunction(template, typeArguments, scope);

	/// <summary>Resolves an interface-parameterized function template name without owning its monomorphization.</summary>
	private string? ResolveInterfaceFunctionTemplateName(string name, SymbolTable scope) => resolveInterfaceFunctionTemplateName(name, scope);

	/// <summary>Delegates interface-call monomorphization to the validation orchestrator.</summary>
	private FunctionSymbol? TryResolveInterfaceCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argumentTypes, SymbolTable scope)
		=> tryResolveInterfaceCall(call, argumentTypes, scope);

	/// <summary>Resolves a protocol-parameterized function template name without owning its monomorphization.</summary>
	private string? ResolveProtocolFunctionTemplateName(string name, SymbolTable scope) => resolveProtocolFunctionTemplateName(name, scope);

	/// <summary>Delegates protocol-call monomorphization to the validation orchestrator.</summary>
	private FunctionSymbol? TryResolveProtocolCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argumentTypes, SymbolTable scope)
		=> tryResolveProtocolCall(call, argumentTypes, scope);

	/// <summary>Uses the validation pass's existing terminal-return classification for lambda block bodies.</summary>
	private bool EndsWithReturn(SyntaxNode node) => endsWithReturn(node);

	/// <summary>
	/// Validates one expression and records any semantic bindings required by later compiler phases.
	/// </summary>
	public void Check(ExpressionSyntax expr, SymbolTable scope)
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
				Check(heap.Expression, scope);
				break;
			case HeapArrayAllocationExpressionSyntax heapArr:
				Check(heapArr.CountExpression, scope);
				if (GetType(heapArr.CountExpression, scope) is { } heapCountTy && !heapCountTy.Equals(TypeSymbol.Int))
				{
					context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], heapArr.CountExpression.Span, "Heap array allocation size must be an integer.");
				}

				break;
			case ArrayInitializationExpressionSyntax arrInit:
				foreach (var el in arrInit.Elements)
					Check(el, scope);
				break;
			case ArrayReplicationExpressionSyntax arrRepl:
				Check(arrRepl.Value, scope);
				Check(arrRepl.Count, scope);
				if (GetType(arrRepl.Count, scope) is { } countTy && !countTy.Equals(TypeSymbol.Int))
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

						Check(arg, scope);
						var argType = GetType(arg, scope) ?? TypeSymbol.Int;
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
							Check(bin.Right, scope);
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
						Check(bin.Right, scope);

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

								CheckEnumIntMismatch(varSymbol.Type, GetType(bin.Right, scope), bin.Span);
							}
							else
							{
								var currentFileContext = context.FileContexts[context.CurrentUnit!];
								context.Diagnostics.Report(currentFileContext, id.Span, $"Undefined variable '{id.Name}'");
							}
						}
						else
						{
							Check(bin.Left, scope);
						}
					}
					else
					{
						Check(bin.Left, scope);
						Check(bin.Right, scope);
						CheckEnumIntMismatch(GetType(bin.Left, scope), GetType(bin.Right, scope), bin.Span);

						var binLeftType = GetType(bin.Left, scope);
						var binRightType = GetType(bin.Right, scope);
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
				InlineAsm.Validate(asmExpr, scope);
				break;
			case NameofExpressionSyntax nameofExpr:
				Intrinsics.ValidateNameof(nameofExpr, scope);
				break;
			case TypeofExpressionSyntax typeofExpr:
				Intrinsics.ValidateTypeof(typeofExpr);
				break;
			case UnaryExpressionSyntax unary:
				Check(unary.Operand, scope);
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

	/// <summary>
	/// Validates member access and returns the resolved semantic member type when available.
	/// </summary>
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

		Check(expr.Expression, scope);
		var leftType = GetType(expr.Expression, scope);
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

	/// <summary>
	/// Finds the compilation unit that declares a semantic type, including instantiated generic templates.
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
	/// Enumerates progressively less-instantiated generic names used to locate a declaring template.
	/// </summary>
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

	/// <summary>
	/// Reconstructs a dotted identifier/member-access chain when the expression is purely a qualified name.
	/// </summary>
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

	/// <summary>
	/// Validates named struct or union initialization and returns the constructed semantic type.
	/// </summary>
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
				Check(init.Expression, scope);
			}

			var initType = GetType(init.Expression, scope);
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
				CheckDelegateValue(init.Expression, delegateFieldType, scope);
			}
			else
			{
				Check(init.Expression, scope);
			}

			// Delegate fields are target-typed against the declared member type; the
			// group/lambda check above already verified assignability.
			var initType = field.Type is DelegateTypeSymbol ? null : GetType(init.Expression, scope);
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

	/// <summary>
	/// Infers and validates the semantic type of an array literal.
	/// </summary>
	private TypeSymbol? CheckArrayInitialization(ArrayInitializationExpressionSyntax expr, SymbolTable scope)
	{
		if (expr.Elements.Count == 0)
			return null; // Can't infer type of empty array easily yet

		var elementType = GetType(expr.Elements[0], scope) ?? TypeSymbol.Int;

		for (var i = 1; i < expr.Elements.Count; i++)
		{
			var elType = GetType(expr.Elements[i], scope);
			if (elType is not null && !elType.Equals(elementType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Elements[i].Span, $"Array elements must have the same type. Expected '{elementType.Name}', found '{elType.Name}'");
			}
		}

		return new ArrayTypeSymbol(elementType, expr.Elements.Count);
	}

	/// <summary>
	/// Validates a borrow expression and returns the resulting reference type.
	/// </summary>
	private TypeSymbol? CheckBorrowExpression(BorrowExpressionSyntax expr, SymbolTable scope)
	{
		Check(expr.Expression, scope);
		var innerType = GetType(expr.Expression, scope);
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

	/// <summary>
	/// Returns whether an expression denotes storage that may be mutably borrowed or assigned.
	/// </summary>
	private bool IsExpressionMutable(ExpressionSyntax expr, SymbolTable scope)
	{
		// 1. In unsafe context / [UnsafeBody], raw pointer dereferences are mutable l-values
		if (_validation.UnsafeDepth > 0 && expr is UnaryExpressionSyntax { Operator: "*" })
			return true;

		if (expr is UnaryExpressionSyntax { Operator: "*" } deref)
		{
			var opType = GetType(deref.Operand, scope);
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
			var targetType = GetType(m.Expression, scope);
			if (targetType is RawPointerTypeSymbol)
				return true;
			if (targetType is PointerTypeSymbol ptr)
				return ptr.IsMutable;

			return IsExpressionMutable(m.Expression, scope);
		}

		if (expr is IndexExpressionSyntax idx)
		{
			var parentType = GetType(idx.Left, scope);
			if (parentType is SliceTypeSymbol or RawPointerTypeSymbol)
				return true;

			return IsExpressionMutable(idx.Left, scope);
		}

		return false;
	}

	/// <summary>
	/// Validates a conditional expression and returns its common result type when compatible.
	/// </summary>
	private TypeSymbol? CheckTernaryExpression(TernaryExpressionSyntax expr, SymbolTable scope)
	{
		Check(expr.Condition, scope);
		var condType = GetType(expr.Condition, scope);
		if (condType is not null && !condType.Equals(TypeSymbol.Bool))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Condition.Span, $"Ternary condition must be 'bool', found '{condType.Name}'");
		}

		Check(expr.ThenExpression, scope);
		Check(expr.ElseExpression, scope);

		var thenType = GetType(expr.ThenExpression, scope);
		var elseType = GetType(expr.ElseExpression, scope);

		if (thenType is not null && elseType is not null && !thenType.Equals(elseType))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Ternary branches must have the same type. Found '{thenType.Name}' and '{elseType.Name}'");
		}

		return thenType;
	}

	/// <summary>
	/// Returns the semantic result type used by validation for an already parsed expression.
	/// </summary>
	public TypeSymbol? GetType(ExpressionSyntax expr, SymbolTable scope)
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
			BorrowExpressionSyntax b => new PointerTypeSymbol(GetType(b.Expression, scope) ?? TypeSymbol.Int, b.IsMutable),
			StructInitializationExpressionSyntax s => CheckStructInitializationExpression(s, scope),
			HeapAllocationExpressionSyntax h => GetType(h.Expression, scope),
			HeapArrayAllocationExpressionSyntax ha => new SliceTypeSymbol(context.ResolveType(ha.ElementTypeName)!),
			IndexExpressionSyntax idx => (GetType(idx.Left, scope) as ArrayTypeSymbol)?.ElementType,
			ArrayInitializationExpressionSyntax a => CheckArrayInitialization(a, scope),
			ArrayReplicationExpressionSyntax r => CheckArrayReplication(r, scope),
			ParenthesizedStructInitializerExpressionSyntax p => p.ResolvedStructTypeName is not null ? context.ResolveType(p.ResolvedStructTypeName) : null,
			TernaryExpressionSyntax t => CheckTernaryExpression(t, scope),
			VoidLiteralExpressionSyntax => TypeSymbol.Void,
			DefaultExpressionSyntax d => context.ResolveType(d.TypeName),
			UnaryExpressionSyntax unary => GetUnaryExpressionType(unary, scope),
			AsmExpressionSyntax asm => InlineAsm.GetResultType(asm, scope),
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
	public void CheckTargetTypedLambda(LambdaExpressionSyntax lam, DelegateTypeSymbol delegateType, SymbolTable scope)
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
			Check(lam.ExpressionBody, lambdaScope);
			var bodyType = GetType(lam.ExpressionBody, lambdaScope);
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
	public void CheckFunctionGroupConversion(ExpressionSyntax groupRef, DelegateTypeSymbol delegateType, SymbolTable scope)
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
					var receiverType = GetType(ma.Expression, scope);
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
	public void CheckDelegateValue(ExpressionSyntax expr, DelegateTypeSymbol delegateType, SymbolTable scope)
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
			Check(expr, scope);
		}
	}

	/// <summary>True if the member access names a zero-arg-this extension member on the
	/// receiver's type (a bound-method group) rather than a struct/union field.</summary>
	public bool IsMethodGroupReference(MemberAccessExpressionSyntax ma, SymbolTable scope)
	{
		// Namespace-qualified globals are values, not method groups. Resolve the full
		// access before inspecting its receiver so namespace prefixes are not checked
		// as ordinary variables (for example: System.Math.Int.MaxValue).
		if (TryResolveNamespaceGlobal(ma, out _))
			return false;

		var receiverType = GetType(ma.Expression, scope);
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

	/// <summary>
	/// Returns the enum type preserved by synthesized flags binary operators.
	/// </summary>
	private TypeSymbol? GetFlagsBinaryType(BinaryExpressionSyntax bin, SymbolTable scope)
	{
		// (§3.B) Typing for the synthesized [Flags] operators: '|', '&', '^' preserve the
		// enum type. Mixed/int operands are caught by CheckEnumIntMismatch during
		// CheckExpression; for pure-integer binaries keep the historical null result
		// (vars fall back to int) so nothing changes for non-enum code.
		var left = GetType(bin.Left, scope);
		var right = GetType(bin.Right, scope);
		return left is EnumTypeSymbol ? left : right is EnumTypeSymbol ? right : null;
	}


	/// <summary>
	/// Returns the root identifier of a nested member or borrow expression when present.
	/// </summary>
	public string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
			return id.Name;
		if (expr is MemberAccessExpressionSyntax m)
			return GetBaseIdentifierName(m.Expression);
		if (expr is BorrowExpressionSyntax b)
			return GetBaseIdentifierName(b.Expression);
		return null;
	}

	/// <summary>
	/// Validates array replication and returns the resulting fixed-array type.
	/// </summary>
	private TypeSymbol? CheckArrayReplication(ArrayReplicationExpressionSyntax expr, SymbolTable scope)
	{
		var valueType = GetType(expr.Value, scope) ?? TypeSymbol.Int;
		if (expr.Count is IntegerLiteralExpressionSyntax countLit)
		{
			return new ArrayTypeSymbol(valueType, checked((int)countLit.Value));
		}

		return new ArrayTypeSymbol(valueType, 0);
	}

	/// <summary>
	/// Validates positional aggregate initialization against declaration field order.
	/// </summary>
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
				Check(init.Expression, scope);
			}
		}
	}

	/// <summary>
	/// Reports passing a large union by value where the current calling rules forbid it.
	/// </summary>
	private void CheckLargeUnionByValueArgument(ExpressionSyntax arg, TypeSymbol? paramType, SymbolTable scope)
	{
		if (paramType is PointerTypeSymbol)
			return;

		var type = GetType(arg, scope);
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

	/// <summary>
	/// Reports invalid implicit mixing between enum and integer operands.
	/// </summary>
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

	/// <summary>
	/// Returns whether an expression is a compile-time string concatenation tree.
	/// </summary>
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

	/// <summary>
	/// Validates an explicit unary cast and reports unsupported or unsafe conversions.
	/// </summary>
	private void CheckUnaryCast(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		if (!unary.Operator.StartsWith('(') || !unary.Operator.EndsWith("*)") || unary.Operator.Length < 4)
			return;

		var operandType = GetType(unary.Operand, scope);
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

	/// <summary>
	/// Validates an is-pattern expression and records any promoted binding.
	/// </summary>
	private void CheckIsPatternExpression(IsPatternExpressionSyntax isPat, SymbolTable scope)
	{
		Check(isPat.Operand, scope);
		var operandType = GetType(isPat.Operand, scope);
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

	/// <summary>
	/// Validates bitwise complement on a flags enum.
	/// </summary>
	private void CheckUnaryEnumTilde(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		// (§3.B) `~` is only meaningful for [Flags] enums, where it is the masked bitwise
		// complement (~v & CombinedAtomicMask).
		if (unary.Operator != "~")
			return;
		if (GetType(unary.Operand, scope) is not EnumTypeSymbol enumType)
			return;
		if (enumType.IsFlags)
			return;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, unary.Span,
			$"Operator '~' cannot be applied to non-[Flags] enum '{enumType.Name}'.");
	}

	/// <summary>
	/// Returns the semantic result type produced by a unary expression.
	/// </summary>
	private TypeSymbol? GetUnaryExpressionType(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		if (unary.Operator == "&")
		{
			var opType = GetType(unary.Operand, scope);
			return opType is not null ? new RawPointerTypeSymbol(opType) : null;
		}

		if (unary.Operator == "*")
		{
			var opType = GetType(unary.Operand, scope);
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
				var operandType = GetType(unary.Operand, scope);
				if (operandType is not EnumTypeSymbol && TypeSymbol.IsIntegerType(operandType))
					return context.ResolveType($"Option<{castEnum.Name}>") ?? result;
			}

			return result;
		}

		// (§3.B) `~` on a [Flags] enum yields the enum again (inverted bits, masked at codegen).
		if (unary.Operator == "~")
		{
			if (GetType(unary.Operand, scope) is EnumTypeSymbol enumType)
				return enumType;
		}

		return GetType(unary.Operand, scope);
	}

	/// <summary>
	/// Emits CVL1012 warning if a function or type marked '[MustUse]' is called as an unused standalone statement.
	/// </summary>
	public void CheckMustUseDiscard(ExpressionSyntax expr, SymbolTable scope)
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
}
