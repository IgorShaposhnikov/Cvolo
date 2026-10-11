using Cvolo.Analysis.Formatting;
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
/// delegated to dedicated validators. Explicit generic and interface/protocol monomorphization are delegated
/// to <see cref="GenericFunctionInstantiator"/>, while block validation returns to the single shared traversal.
/// This service does not create a second AST pass or depend on <see cref="ValidationPass"/>.
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
	GenericFunctionInstantiator generics,
	Func<SyntaxNode, bool> endsWithReturn)
{
	private readonly ValidationContext _validation = validation;
	private FormattingResolver? _formattingInstance;
	private FormattingResolver Formatting
	{
		get
		{
			return _formattingInstance ??= new(context, overloads, NamesNamespace, (expr, scope) => Check(expr, scope), (expr, scope) => GetType(expr, scope));
		}
	}

	private ClassificationAnalyzer Classification => classification;
	private OverloadResolver Overloads => overloads;
	private CallResolver Calls => calls;
	private InlineAsmValidator InlineAsm => inlineAsm;
	private IntrinsicValidator Intrinsics => intrinsics;
	private GenericFunctionInstantiator Generics => generics;

	/// <summary>
	/// Delegates lambda block validation back to the single validation traversal.
	/// </summary>
	private void CheckBlock(BlockStatementSyntax? block, SymbolTable scope, FunctionDeclarationSyntax currentFunction)
	{
		validateBlock(block, scope, currentFunction);
	}

	/// <summary>
	/// Uses the validation pass's existing terminal-return classification for lambda block bodies.
	/// </summary>
	private bool EndsWithReturn(SyntaxNode node)
	{
		return endsWithReturn(node);
	}

	/// <summary>
	/// Validates one expression and records any semantic bindings required by later compiler phases.
	/// </summary>
	/// <param name="expr">The expression to validate.</param>
	/// <param name="scope">The scope the expression is evaluated in.</param>
	/// <param name="expectedType">
	/// The surrounding target type, when the expression appears in a position that has one (a
	/// declared variable's initializer, a return expression, or the left operand of a fallback).
	/// Only context-typed forms consume it today; most expressions are typed on their own. Passing it
	/// does not relax any check, so a position that omits it simply rejects context-typed forms.
	/// </param>
	public void Check(ExpressionSyntax expr, SymbolTable scope, TypeSymbol? expectedType = null)
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
							if (_validation.AssociatedInstanceFields?.Contains(id.Name) == true)
							{
								context.Diagnostics.Report(currentFileContext, id.Span,
									$"Instance field '{id.Name}' cannot be accessed because the associated function has no receiver.",
									DiagnosticIds.InstanceFieldAccessedFromAssociatedFunction);
							}
							else
							{
								context.Diagnostics.Report(currentFileContext, id.Span, $"Undefined variable '{id.Name}'", DiagnosticIds.UndefinedVariable);
							}
						}
					}

					break;
				}
			case MemberAccessExpressionSyntax memberAccess:
				CheckMemberAccessExpression(memberAccess, scope, expectedType);
				break;
			case BorrowExpressionSyntax borrow:
				CheckBorrowExpression(borrow, scope);
				break;
			case StructInitializationExpressionSyntax structInit:
				GetOrBindStructInitializer(structInit, scope);
				break;
			case CharacterLiteralExpressionSyntax:
				break;
			case NullLiteralExpressionSyntax:
				// The null literal is unsafe-only; record it as the canonical unsafe usage so a
				// [UnsafeBody] whose only operation is `null` is still recognized as effective.
				_validation.RecordUnsafeUsage(UnsafeUsageKind.NullUnsafeLiteral);
				break;
			case HeapAllocationExpressionSyntax heap:
				Check(heap.Expression, scope);
				_validation.RecordUnsafeUsage(UnsafeUsageKind.HeapAllocation);
				break;
			case HeapArrayAllocationExpressionSyntax heapArr:
				Check(heapArr.CountExpression, scope);
				_validation.RecordUnsafeUsage(UnsafeUsageKind.HeapAllocation);
				if (GetType(heapArr.CountExpression, scope) is { } heapCountTy && !heapCountTy.Equals(TypeSymbol.Int))
				{
					context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], heapArr.CountExpression.Span, "Heap array allocation size must be an integer.", DiagnosticIds.HeapArrayAllocationSizeMustBeAnInteger);
				}

				break;
			case IndexExpressionSyntax index:
				// Indexing is an expression container. Its children must be validated before any
				// surrounding expression is typed; otherwise a binary index such as a[i - 1]
				// can escape canonical binding and first reach LLVM codegen unbound.
				Check(index.Left, scope);
				Check(index.Index, scope);
				CheckRawPointerIndex(index, scope);
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
					context.Diagnostics.Report(currentFileContext, arrRepl.Count.Span, "Array replication count must be an integer.", DiagnosticIds.ArrayReplicationCountMustBeAnInteger);
				}

				break;
			case ParenthesizedStructInitializerExpressionSyntax parenStruct:
				CheckParenthesizedStructInitialization(parenStruct, scope);
				break;
			case TernaryExpressionSyntax ternary:
				CheckTernaryExpression(ternary, scope);
				break;
			case CoalesceExpressionSyntax coalesce:
				GetOrBindCoalesceExpression(coalesce, scope, expectedType);
				break;
			case OptionalChainExpressionSyntax optionalChain:
				GetOrBindOptionalChain(optionalChain, scope);
				break;
			case CallExpressionSyntax call:
				{
					// §7.1 formatter-directed interpolation binds BEFORE ordinary overload resolution.
					// A formatter.Write with a single interpolated string argument is a sequence of
					// sink writes, never a materialized string passed to Write(string).
					if (TryBindFormatterInterpolation(call, scope))
						return;

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
							UnaryExpressionSyntax { Operator: "&" } addressArg => IsFunctionAddressOperand(addressArg.Operand, scope),
							_ => false,
						};
						if (isDeferredGroup)
						{
							deferredGroupArgs.Add((arg, argIndex));
							argTypes.Add(OverloadResolver.DeferredCallableArgument);
							continue;
						}

						Check(arg, scope);
						var argType = GetType(arg, scope);
						if (argType is null
							&& ((arg is BinaryExpressionSyntax binaryArgument
							&& context.ResolvedBinaryExpressions.TryGetValue(binaryArgument, out var binaryBinding)
							&& binaryBinding.State == BinaryBindingState.Error)
							|| arg is StructInitializationExpressionSyntax))
						{
							argTypes.Add(OverloadResolver.UnresolvedArgument);
						}
						else
						{
							// Preserve legacy fallback behavior for non-binary expression families; this INC
							// deliberately changes only canonical BinaryExpression binding.
							argTypes.Add(argType ?? TypeSymbol.Int);
						}
					}

					// An argument with a final semantic error must not be disguised as a primitive just so
					// overload resolution can continue. Its own diagnostic is authoritative; suppress the
					// secondary "no overload" cascade for this call shape.
					if (argTypes.Any(type => ReferenceEquals(type, OverloadResolver.UnresolvedArgument)))
						return;

					// Canonical Base Formatter.Write is the sink surface of the formatting pipeline: its
					// declared overloads are empty stubs whose bodies never execute, so every value call
					// (primitive, string, IFormattable, IStringable, Option/Result) must bind a canonical
					// formatting operation BEFORE ordinary overload resolution. Console keeps ordinary
					// overloads (they are the printf backend) and falls back to the pipeline only when
					// ordinary resolution finds nothing (§7.2 in the Func-is-null block below).
					if (TryBindFormatterValueCall(call, scope, argTypes))
						return;

					FunctionSymbol? func = null;
					var mismatch = DottedCallMismatch.None;
					string? mismatchOwnerName = null;

					// Callable value paths take precedence over direct function/function-group lookup.
					// This is required for both safe and native delegate variables that collide with
					// a function of the same source name.
					if (TryBindDelegateInvocation(call, scope, argTypes, deferredGroupArgs))
						break;

					if (call.TypeArguments.Count > 0)
					{
						// A generic extension member is reached through its owner type. Instantiate it first so a
						// concrete candidate lands in the owner's overload table, then let the ordinary
						// receiver-splitting overload path resolve the call against it.
						var extensionMember = TryInstantiateGenericExtensionMember(call, argTypes, scope);
						if (extensionMember is not null)
						{
							func = extensionMember == true
								? ResolveInstantiatedExtensionMember(call, argTypes, scope, out mismatch, out mismatchOwnerName)
								: null;
						}
						else
						{
							// Open generic template binding must not materialize a concrete owner specialization.
							// `Result<T, E>(value)` inside `extension Result<T, E>` is valid template syntax, but
							// constructing a synthetic `Result<T,E>` runtime type here would enqueue bodies whose T/E
							// cease to exist after the template scope is popped. Defer the constructor target until the
							// enclosing owner is monomorphized with concrete arguments.
							var writtenTypeArgs = call.TypeArguments
								.Select(context.ResolveType)
								.ToList();
							var hasOpenTypeArgument = writtenTypeArgs.Any(t => t is not null && ContainsTypeParameter(t));
							var openOwner = hasOpenTypeArgument ? context.ResolveType(call.FunctionName) : null;
							var isOpenGenericConstructor = openOwner is not null
								&& (context.GenericStructTemplates.ContainsKey(openOwner.Name)
									|| context.GenericUnionTemplates.ContainsKey(openOwner.Name));

							if (isOpenGenericConstructor)
								return;

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
								// Fallback to standard generic function monomorphization. An open T/E/... argument is
								// deferred for the same reason as an open generic constructor above.
								var templateName = Generics.ResolveFunctionTemplateName(call.FunctionName, scope);
								if (templateName != null && context.GenericFunctionTemplates.TryGetValue(templateName, out var templateDecl))
								{
									var typeArgs = call.TypeArguments.Select(context.ResolveType).ToList();
									if (typeArgs.Any(t => t is not null && ContainsTypeParameter(t)))
										return;

									if (typeArgs.All(static t => t is not null))
										func = Generics.InstantiateGenericFunction(templateDecl, [.. typeArgs.Select(static t => t!)], scope);
								}
							}
						}
					}
					else
					{
						// Use overload resolution logic for standard non-generic functions / constructors
						func = Calls.ResolveOrdinaryCall(call, argTypes, scope, out mismatch, out mismatchOwnerName, out var ambiguousOverload);

						// Two or more distinct applicable candidates were equally best: this is
						// ambiguity, not a missing overload. Reporting it before the generic/template
						// fallbacks keeps the defect precise and prevents a guessed binding.
						if (func is null && ambiguousOverload)
						{
							context.Diagnostics.Report(
								context.FileContexts[context.CurrentUnit!],
								call.ArgumentListSpan,
								$"Call to '{call.FunctionName}' is ambiguous between multiple overloads.",
								DiagnosticIds.AmbiguousFunctionConversion);
							return;
						}



						// No concrete overload matched: fall back to interface-parameterized dispatch
						// (implicit generic templates monomorphized with the concrete conforming arg types).
						if (func is null && Generics.ResolveInterfaceFunctionTemplateName(call.FunctionName, scope) is not null)
						{
							// The callee is an interface template: specific conformance/arg-count
							// diagnostics are reported inside. Return early so the generic
							// "no overload" message is not also emitted.
							func = Generics.TryResolveInterfaceCall(call, argTypes, scope);
							if (func is null)
								return;
						}

						// No concrete overload matched: fall back to protocol-parameterized
						// dispatch (structural duck typing against the protocol's canonical
						// member tokens; monomorphized with the structurally conforming arg types).
						if (func is null && Generics.ResolveProtocolFunctionTemplateName(call.FunctionName, scope) is not null)
						{
							// The callee is a protocol template: specific structural-conformance/
							// arg-count diagnostics are reported inside. Return early so the
							// generic "no overload" message is not also emitted.
							func = Generics.TryResolveProtocolCall(call, argTypes, scope);
							if (func is null)
								return;
						}

						// No concrete overload matched: the callee may name a generic extension
						// member whose type arguments were omitted. Resolving it here keeps the
						// arity/inference finding specific ("expects a type argument") instead of
						// the generic "no overload matches" mismatch. A template that reported an
						// arity/inference problem returns false, so the call is resolved as
						// unresolved and the generic overload message is suppressed.
						if (func is null)
						{
							var extensionMember = TryInstantiateGenericExtensionMember(call, argTypes, scope);
							if (extensionMember is not null)
							{
								func = extensionMember == true
									? ResolveInstantiatedExtensionMember(call, argTypes, scope, out mismatch, out mismatchOwnerName)
									: null;
								if (func is null)
									return;
							}
						}
					}

					// §11: omitted type arguments on a top-level generic function are inferred from the
					// argument types (Inc - Generic Constraint Semantic Parity §10/§11). Substituted
					// constraints participate in applicability: a failing bound makes this candidate
					// non-applicable without poisoning a viable competing overload, and it becomes the
					// primary diagnostic only when nothing else resolves (§11.1).
					BindingContext.GenericConstraintEvaluation? inferredConstraintFailure = null;
					func ??= TryInferTopLevelGenericFunction(call, argTypes, scope, out inferredConstraintFailure);

					if (func is null)
					{
						// §7.2 Canonical formatting fallback: Console.Write/WriteLine(value) and
						// formatter.Write(value) with no applicable ordinary overload bind a
						// formatting operation (or the dedicated 'not formattable' diagnostic).
						// Formatting contexts take priority over the generic receiver/member
						// diagnostics below so Console.Write(someStruct) reports the formatting
						// finding, not CVL4084.
						if (TryBindFormattingFallback(call, scope, argTypes))
							return;

						// A dotted call that picked the wrong receiver form gets a dedicated
						// diagnostic so the fix ('call it through the type' / 'call it on a value')
						// is explicit instead of hidden behind a generic arity/type mismatch.
						if (mismatch != DottedCallMismatch.None
							&& ReportDottedCallMismatch(call, mismatch, mismatchOwnerName))
						{
							return;
						}

						// A receiver the compiler cannot name at all is the finding, not its arguments.
						// Nothing behind the dot was ever a candidate, so an argument-mismatch report
						// here would read as a second independent defect and point the reader at the
						// wrong line. Reporting the receiver instead keeps the cascade out of the list.
						if (ReportUnresolvedCallReceiver(scope, call))
							return;

						// A receiver that names a real aggregate type but has no such member is a
						// member finding: the call site exists, the member does not. Continuing into
						// overload resolution would fabricate candidates and report an argument
						// mismatch against a member that was never declared.
						if (ReportMissingMemberOnKnownReceiver(call))
							return;

						// A bare callee that names a type is not a call: a Cvolo type is not
						// callable (construction is written as a struct initializer). Report the
						// type rather than fabricating a function overload mismatch.
						if (!call.FunctionName.Contains('.', StringComparison.Ordinal)
							&& (context.ResolveType(context.GetMangledName(call.FunctionName, context.CurrentUnit?.NamespaceDeclaration?.Name))
								?? context.ResolveType(call.FunctionName)) is StructTypeSymbol)
						{
							if (context.GenericParameterScopes.Count > 0)
								return;

							context.Diagnostics.Report(
								context.FileContexts[context.CurrentUnit!],
								call.ArgumentListSpan,
								$"Type '{call.FunctionName}' is not callable; construct it with a struct initializer.",
								DiagnosticIds.TypeIsNotCallable);
							return;
						}

						var currentFileContext = context.FileContexts[context.CurrentUnit!];

						// §11.1: when the only reason no candidate resolved is a failed generic
						// constraint, that failure is the primary diagnostic — not a generic
						// "no overload" mismatch that hides the real defect.
						if (inferredConstraintFailure is { } constraintFailure)
						{
							if (constraintFailure.UnknownContract)
								context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan,
									$"Unknown contract '{constraintFailure.SubstitutedText}' in constraint for parameter '{constraintFailure.ParamName}'.",
									DiagnosticIds.UnknownContractInConstraintForParameter);
							else
								context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan,
									$"Type '{constraintFailure.TypeName}' does not satisfy required constraint '{constraintFailure.SubstitutedText}' for type parameter '{constraintFailure.ParamName}'.",
									DiagnosticIds.TypeDoesNotSatisfyConstraintOfGenericParameter);
							return;
						}

						var sigString = string.Join(", ", argTypes.Select(t => t.Name));
						if (context.GenericParameterScopes.Count > 0)
							return;
						var diagnosticId = call.FunctionName.Contains('.', StringComparison.Ordinal)
							? DiagnosticIds.NoOverloadOfFunctionMatchesArgumentTypes
							: DiagnosticIds.UnresolvedFunctionCall;
						context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan, $"No overload of function '{call.FunctionName}' matches argument types ({sigString})", diagnosticId);
						return;
					}

					// Record the resolved overload for CodeGenerator consumption
					context.ResolvedCalls[call] = func;

					// Target-type any deferred lambda/group arguments against the callee's
					// parameter declarations (§4.2 contextual lambda typing, §22 group conversion).
					if (deferredGroupArgs.Count > 0)
					{
						var isExtensionForDeferred = func.IsInstanceExtension
							|| (func.CallableKind == CallableKind.Free && func.Parameters.Count > 0 && func.Parameters[0].Name == "this");

						foreach (var (arg, argIndex) in deferredGroupArgs)
						{
							var paramIndex = isExtensionForDeferred ? argIndex + 1 : argIndex;
							if (paramIndex >= func.Parameters.Count)
								continue;
							var paramType = func.Parameters[paramIndex].Type;

							if (paramType is DelegateTypeSymbol delegateParamType)
							{
								CheckDelegateValue(arg, delegateParamType, scope);
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
					if (func.SafetyTier == SafetyTier.Unsafe && !func.IsUnsafeBody)
					{
						var isNativeAbiCall = func.IsNativeAbi;
						_validation.RequireUnsafe(
							context,
							call,
							isNativeAbiCall ? UnsafeUsageKind.NativeAbiCall : UnsafeUsageKind.RawUnsafeCall,
							isNativeAbiCall
								? $"Calling native-ABI function '{call.FunctionName}' requires an unsafe context."
								: $"Calling a raw 'unsafe function' '{call.FunctionName}' from code that is not in an unsafe context.",
							isNativeAbiCall ? DiagnosticIds.NativeAbiFunctionCallRequiresUnsafe : DiagnosticIds.CallUnsafeFromSafe);
					}

					var argCount = call.Arguments.Count;
					var paramCount = func.Parameters.Count;
					var isVariadic = func.IsVariadic;

					// An associated function is receiverless, so argument positions map 1:1 onto the
					// declared parameters; only receiver-backed callables shift by the synthetic `this`.
					var isExtensionCall = !func.IsAssociated
						&& (func.IsInstanceExtension || (func.Parameters.Count > 0 && func.Parameters[0].Name == "this"));
					var expectedParamCount = isExtensionCall ? paramCount - 1 : paramCount;


					if (!isVariadic && argCount != expectedParamCount)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan, $"Function '{call.FunctionName}' expects {expectedParamCount} arguments but received {argCount}", DiagnosticIds.FunctionExpectsArgumentsButReceived);
						return;
					}

					if (isVariadic && argCount < paramCount)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan, $"Function '{call.FunctionName}' expects at least {paramCount} arguments but received {argCount}", DiagnosticIds.FunctionExpectsAtLeastArgumentsButReceived);
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
						// Conditional writeback (`user?.name = ...`) is deferred. The desugared compound
						// form (`state?.counter += 1`) also has an outer `=` whose left is the optional
						// chain, so both are caught here.
						if (bin.Left is OptionalChainExpressionSyntax writebackTarget)
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(currentFileContext, writebackTarget.Span,
								"An optional-chained member is not an assignment target; conditional writeback is not supported.",
								DiagnosticIds.OptionalChainIsNotAssignable);
							Check(bin.Right, scope);
							Check(bin.Left, scope);
							break;
						}

						// Handle discard assignment: _ = Func();
						if (bin.Left is IdentifierExpressionSyntax discardId && discardId.Name == "_")
						{
							Check(bin.Right, scope);
							_ = GetOrBindBinaryExpression(bin, scope, final: true);
							break;
						}

						// Delegate-typed assignment targets: a lambda / function-group RHS is
						// target-typed against the assigned variable's delegate type (§4.2 / §22).
						// Any delegate-valued l-value (local/global/member path) supplies the expected
						// type for lambdas, function groups, native &Function, and contextual null.
						// This is intentionally not restricted to simple identifiers: native callback
						// slots are commonly stored in globals and dispatch-table struct fields.
						if (GetType(bin.Left, scope) is DelegateTypeSymbol assigneeDelegate)
						{
							var isTargetTypedDelegateValue = bin.Right is LambdaExpressionSyntax
								|| (bin.Right is IdentifierExpressionSyntax assignGroupId && !Calls.IsKnownVariable(assignGroupId, scope) && Overloads.HasCandidates(assignGroupId.Name))
								|| (bin.Right is MemberAccessExpressionSyntax assignGroupMa && IsMethodGroupReference(assignGroupMa, scope))
								|| (bin.Right is UnaryExpressionSyntax { Operator: "&" } assignAddress && IsFunctionAddressOperand(assignAddress.Operand, scope))
								|| bin.Right is NullLiteralExpressionSyntax;
							if (isTargetTypedDelegateValue)
							{
								CheckDelegateValue(bin.Right, assigneeDelegate, scope);
								_ = GetOrBindBinaryExpression(bin, scope, final: true);
								break;
							}
						}

						// 1. Evaluate the right-hand side first (reads and moves happen here)
						Check(bin.Right, scope);

						// Plain delegate-value assignment still has nominal/category compatibility rules
						// even when no contextual conversion is involved. Native delegates are nominal,
						// and safe/native representations never convert implicitly.
						if (GetType(bin.Left, scope) is DelegateTypeSymbol assignmentDelegate
							&& GetType(bin.Right, scope) is DelegateTypeSymbol assignedDelegate)
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							if (assignmentDelegate.IsNative != assignedDelegate.IsNative)
							{
								context.Diagnostics.Report(currentFileContext, bin.Right.Span,
									"Conversion between safe delegates and native delegates is not defined.",
									DiagnosticIds.SafeNativeDelegateConversion);
							}
							else if (assignmentDelegate.IsNative && !assignmentDelegate.Equals(assignedDelegate))
							{
								context.Diagnostics.Report(currentFileContext, bin.Right.Span,
									$"Implicit conversion between distinct native delegate types '{assignedDelegate.Name}' and '{assignmentDelegate.Name}' is not allowed.",
									DiagnosticIds.InvalidFunctionConversion);
							}
						}

						// 2. Evaluate the left-hand side second (re-initialization happens here)
						if (bin.Left is IdentifierExpressionSyntax id)
						{
							var varSymbol = scope.Lookup(id.Name) as VariableSymbol
								?? context.ResolveGlobalReference(id.Name, out _);
							if (varSymbol is not null)
							{
								// A reference's write capability comes from ref/refvar, not the
								// mutability of the local slot which stores its pointer.
								// Reference *rebinding* writes the pointer slot; assigning an
								// ordinary value through the reference writes the referent.
								// A writable `var ref T` or `global ref T` may rebind even
								// though it cannot mutate the referenced T.
								var rebindsReference = varSymbol.Type is PointerTypeSymbol &&
									(GetType(bin.Right, scope) is PointerTypeSymbol ||
									 bin.Right is BorrowExpressionSyntax);
								// A local `refvar T` binding remains writable even when the
								// source declaration did not use the separate `var` keyword.
								// For `ref T`, only an explicitly mutable pointer slot (`var ref T`)
								// can be rebound; writes through `ref T` remain forbidden.
								var isMutable = varSymbol.Type is PointerTypeSymbol ptr
									? ptr.IsMutable || (rebindsReference && varSymbol.IsMutable)
									: varSymbol.IsMutable;
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
										context.Diagnostics.Report(currentFileContext, id.Span, $"Cannot assign to immutable variable '{id.Name}'", DiagnosticIds.CannotAssignToImmutableVariable);
									}
								}

								CheckEnumIntMismatch(varSymbol.Type, GetType(bin.Right, scope), bin.Span);
							}
							else
							{
								var currentFileContext = context.FileContexts[context.CurrentUnit!];
								if (_validation.AssociatedInstanceFields?.Contains(id.Name) == true)
								{
									context.Diagnostics.Report(currentFileContext, id.Span,
										$"Instance field '{id.Name}' cannot be accessed because the associated function has no receiver.",
										DiagnosticIds.InstanceFieldAccessedFromAssociatedFunction);
								}
								else
								{
									context.Diagnostics.Report(currentFileContext, id.Span, $"Undefined variable '{id.Name}'", DiagnosticIds.UndefinedVariable);
								}
							}
						}
						else
						{
							Check(bin.Left, scope);

							// Reject writes only through an explicitly read-only access path.
							// A bare typed local (e.g. `Player p`) is not an explicit `val`:
							// its fields remain mutable under the existing v0.0.21 contract.
							// Keep copied slice capabilities outside INC A.
							if ((bin.Left is MemberAccessExpressionSyntax ||
								bin.Left is IndexExpressionSyntax { Left: var indexedBase } &&
								GetType(indexedBase, scope) is ArrayTypeSymbol) &&
							IsExplicitReadOnlyPath(bin.Left, scope))
							{
								var currentFileContext = context.FileContexts[context.CurrentUnit!];
								context.Diagnostics.Report(currentFileContext, bin.Left.Span,
									"Cannot assign through a read-only reference or immutable access path.",
									DiagnosticIds.CannotAssignToImmutableVariable);
							}
						}
					}
					else
					{
						Check(bin.Left, scope);
						Check(bin.Right, scope);

						// Binary operator semantics are selected exactly once after both operands have
						// themselves been validated. The final descriptor is then reused by overload
						// resolution, tooling, and code generation.
						_ = GetOrBindBinaryExpression(bin, scope, final: true);

						// Statically-known invalid integer edges (zero divisor, signed MIN / -1,
						// out-of-domain shift count) are rejected before code generation by the shared
						// integer-edge classifier over that same canonical descriptor.
						ReportIntegerEdgeConstantFailure(bin, scope);
					}

					// Assignment is compiler-owned rather than overloadable, but it is still a
					// BinaryExpressionSyntax and therefore receives a canonical descriptor too.
					if (bin.Operator == "=")
						_ = GetOrBindBinaryExpression(bin, scope, final: true);

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
			case SizeofExpressionSyntax sizeofExpr:
				ValidateLayoutQuery(sizeofExpr, sizeofExpr.TypeName, wantAlignment: false);
				break;
			case AlignofExpressionSyntax alignofExpr:
				ValidateLayoutQuery(alignofExpr, alignofExpr.TypeName, wantAlignment: true);
				break;
			case OffsetofExpressionSyntax offsetofExpr:
				ValidateOffsetof(offsetofExpr);
				break;
			case UnaryExpressionSyntax unary:
				// Conditional writeback through an increment/decrement is deferred, exactly as the
				// assignment form is (see the binary `=` case).
				if (unary.Operator is "++_postfix" or "--_postfix" or "++_prefix" or "--_prefix"
					&& unary.Operand is OptionalChainExpressionSyntax mutationTarget)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, mutationTarget.Span,
						"An optional-chained member is not an assignment target; conditional writeback is not supported.",
						DiagnosticIds.OptionalChainIsNotAssignable);
					Check(unary.Operand, scope);
					break;
				}

				if (unary.Operator == "&" && IsFunctionAddressOperand(unary.Operand, scope))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, unary.Span,
						"Function address requires an expected native delegate type.",
						DiagnosticIds.FunctionAddressRequiresContext);
					break;
				}

				Check(unary.Operand, scope);

				// Raw dereference and address-of are unsafe-only operations. Record the canonical
				// usage; the safety validator owns the operation-specific diagnostic so recording
				// here never double-reports.
				if (unary.Operator == "*")
					_validation.RecordUnsafeUsage(UnsafeUsageKind.PointerDereference);
				else if (unary.Operator == "&")
					_validation.RecordUnsafeUsage(UnsafeUsageKind.AddressOf);

				// OPERATOR OVERLOAD (§14): a declared unary operator answers before the builtin
				// cast and [Flags] meanings of '-', '!' and '~'.
				var unaryOperandType = GetType(unary.Operand, scope);
				if (context.FindOperatorOverload(unary.Operator, unaryOperandType) is not null)
					break;

				CheckUnaryCast(unary, scope);
				CheckUnaryEnumTilde(unary, scope);
				ReportMissingOperatorOverload(unary.Operator, unaryOperandType, null, unary: true, unary.Span);
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
							"Cannot infer the type of a bare 'default' expression. Use default(T) or declare the variable with an explicit type.", DiagnosticIds.CannotInferTheTypeOfABareDefaultExpressionUseDefaultTOrDecla);
						break;
					}

					var defaultTy = context.ResolveType(defaultExpr.TypeName);
					if (defaultTy is null)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, defaultExpr.Span, $"Unknown type '{defaultExpr.TypeName}' in default expression", DiagnosticIds.UnknownTypeInDefaultExpression);
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
							context.Diagnostics.Report(currentFileContext, defaultExpr.Span, $"Type '{defaultExpr.TypeName}' cannot be used with default because it is not a Trivial Copy Type", DiagnosticIds.TypeCannotBeUsedWithDefaultBecauseItIsNotATrivialCopyType);
						}
					}
					else if (defaultTy is DelegateTypeSymbol { IsNative: true })
					{
						_validation.RequireUnsafe(context, defaultExpr, UnsafeUsageKind.NullUnsafeLiteral,
							$"default({defaultExpr.TypeName}) creates a null native function pointer and requires unsafe.", DiagnosticIds.NullForNativeDelegate);
					}
					else if (defaultTy is DelegateTypeSymbol { IsNative: false })
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

	private void ValidateLayoutQuery(ExpressionSyntax node, string typeName, bool wantAlignment)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		var queryName = wantAlignment ? "alignof" : "sizeof";
		var diagnosticId = wantAlignment ? DiagnosticIds.AlignofInvalidTypeError : DiagnosticIds.SizeofInvalidTypeError;
		var type = context.ResolveType(typeName);

		if (type is null)
		{
			context.Diagnostics.Report(currentFileContext, node.Span, $"Unknown type '{typeName}' in {queryName} expression.", diagnosticId);
			return;
		}

		if (type.Equals(TypeSymbol.Void))
		{
			context.Diagnostics.Report(currentFileContext, node.Span, $"{queryName} cannot be applied to 'void'.", DiagnosticIds.LayoutQueryOnVoid);
			return;
		}

		if (ContainsTypeParameter(type))
			return;

		if (type is ProtocolTypeSymbol or InterfaceTypeSymbol)
		{
			context.Diagnostics.Report(currentFileContext, node.Span,
				$"Type '{typeName}' has no concrete runtime layout and cannot be used with {queryName}.",
				DiagnosticIds.LayoutQueryIncompleteType);
			return;
		}

		var layout = context.LayoutService.GetLayout(type);
		context.ResolvedLayoutConstants[node] = wantAlignment ? layout.Alignment : layout.Size;
	}

	private void ValidateOffsetof(OffsetofExpressionSyntax node)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		var type = context.ResolveType(node.TypeName);

		if (type is null)
		{
			context.Diagnostics.Report(currentFileContext, node.Span, $"Unknown type '{node.TypeName}' in offsetof expression.", DiagnosticIds.OffsetofInvalidTargetError);
			return;
		}

		if (type.Equals(TypeSymbol.Void) || type is ProtocolTypeSymbol or InterfaceTypeSymbol or EnumTypeSymbol)
		{
			context.Diagnostics.Report(currentFileContext, node.Span, $"Type '{node.TypeName}' does not have addressable stored members.", DiagnosticIds.OffsetofInvalidTargetError);
			return;
		}

		if (ContainsTypeParameter(type))
			return;

		long offset = 0;
		var current = type;

		foreach (var member in node.Members)
		{
			if (current is UnionTypeSymbol { IsUnsafe: false })
			{
				context.Diagnostics.Report(currentFileContext, member.Span, "offsetof cannot address a variant of a safe union.", DiagnosticIds.OffsetofSafeUnionVariant);
				return;
			}

			var step = context.LayoutService.TryGetFieldOffset(current, [member.Name]);
			var next = ResolveStoredMemberType(current, member.Name);

			if (step is null || next is null)
			{
				context.Diagnostics.Report(currentFileContext, member.Span,
					$"Type '{current.Name}' does not contain a stored member named '{member.Name}'.",
					DiagnosticIds.OffsetofMissingMember);
				return;
			}

			offset += step.Value;
			current = next;
		}

		context.ResolvedLayoutConstants[node] = offset;
	}

	private static TypeSymbol? ResolveStoredMemberType(TypeSymbol type, string name) => type switch
	{
		StructTypeSymbol structType => structType.FindField(name)?.Type,
		UnionTypeSymbol unionType => unionType.FindField(name)?.Type,
		_ => null,
	};

	internal static bool ContainsTypeParameter(TypeSymbol type) => ContainsTypeParameter(type, []);

	private static bool ContainsTypeParameter(TypeSymbol type, HashSet<TypeSymbol> active)
	{
		if (type is TypeParameterSymbol)
			return true;
		if (!active.Add(type))
			return false;

		return type switch
		{
			StructTypeSymbol structType => structType.Fields.Any(field => ContainsTypeParameter(field.Type, active)),
			UnionTypeSymbol unionType => unionType.Fields.Any(field => ContainsTypeParameter(field.Type, active)),
			ArrayTypeSymbol arrayType => ContainsTypeParameter(arrayType.ElementType, active),
			SliceTypeSymbol sliceType => ContainsTypeParameter(sliceType.ElementType, active),
			PointerTypeSymbol pointerType => ContainsTypeParameter(pointerType.ReferencedType, active),
			RawPointerTypeSymbol rawPointerType => ContainsTypeParameter(rawPointerType.ElementType, active),
			DelegateTypeSymbol delegateType => delegateType.Parameters.Any(parameter => ContainsTypeParameter(parameter.Type, active))
				|| ContainsTypeParameter(delegateType.ReturnType, active),
			_ => false,
		};
	}

	/// <summary>
	/// Binds a call through a delegate-typed value path before direct function lookup.
	/// </summary>
	private bool TryBindDelegateInvocation(
		CallExpressionSyntax call,
		SymbolTable scope,
		IReadOnlyList<TypeSymbol> argumentTypes,
		IReadOnlyList<(ExpressionSyntax Arg, int Index)> deferredGroupArgs)
	{
		if (!Calls.TryResolveDelegateInvocation(call, scope, out var delegateType) || delegateType is null)
			return false;

		context.ResolvedDelegateCalls[call] = delegateType;

		// Delegate calls do not pass through ordinary function overload resolution, so validate
		// their argument shape explicitly. Reuse the ordinary signature compatibility rules to
		// preserve integer-width/reference conversions and contextual native-delegate null.
		var parameterTypes = delegateType.Parameters.Select(parameter => parameter.Type).ToArray();
		if (Overloads.CompareSignature(parameterTypes, argumentTypes, isVariadic: false) < 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			var actual = string.Join(", ", argumentTypes.Select(type => type.Name));
			context.Diagnostics.Report(currentFileContext, call.ArgumentListSpan,
				$"Delegate '{delegateType.Name}' cannot be invoked with argument types ({actual}).", DiagnosticIds.DelegateCannotBeInvokedWithArgumentTypes);
		}

		if (delegateType.IsNative)
		{
			_validation.RequireUnsafe(
				context,
				call,
				UnsafeUsageKind.NativeDelegateInvocation,
				$"Invoking native delegate '{delegateType.Name}' requires an unsafe context.",
				DiagnosticIds.NativeDelegateInvokeRequiresUnsafe);
		}

		foreach (var (arg, argIndex) in deferredGroupArgs)
		{
			if (argIndex >= delegateType.Parameters.Count)
				continue;
			if (delegateType.Parameters[argIndex].Type is DelegateTypeSymbol parameterDelegate)
			{
				CheckDelegateValue(arg, parameterDelegate, scope);
			}
			else
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, arg.Span,
					"Callable expression requires a delegate-typed parameter at this argument position.",
					DiagnosticIds.LambdaRequiresExpectedDelegateType);
			}
		}

		return true;
	}


	/// <summary>
	/// §7.1 binding hook: <c>formatter.Write($"...")</c> on a canonical Base <c>Formatter</c> value.
	/// Runs before ordinary overload resolution so the interpolation is never typed or materialized
	/// as a string. Returns whether the call was fully handled by the formatter pipeline.
	/// </summary>
	private bool TryBindFormatterInterpolation(CallExpressionSyntax call, SymbolTable scope)
	{
		if (call.Arguments.Count != 1)
			return false;

		if (call.Arguments[0] is not InterpolatedStringExpressionSyntax)
			return false;

		if (!Formatting.TryClassifyContext(call, scope, out var kind) || kind != FormattingContextKind.FormatterWrite)
			return false;

		var interpolated = (InterpolatedStringExpressionSyntax)call.Arguments[0];
		if (Formatting.TryBindFormatterInterpolation(call, scope, out var operation))
			context.ResolvedFormatterInterpolations[call] = operation;

		// Handled either way: an empty interpolation binds a zero-segment operation, and a failed
		// expression segment already received the dedicated diagnostic. The call must not fall
		// through to ordinary Write(string) resolution.
		return true;
	}

	/// <summary>
	/// §7.2 interception for canonical <c>Formatter.Write(value)</c> calls. Runs before ordinary
	/// overload resolution because the Base stub overloads have empty bodies and must never be
	/// selected; every value reaches the canonical formatting pipeline. Returns whether the call
	/// was fully handled (bound, or reported "not formattable"). Console contexts return false so
	/// ordinary Console overloads keep winning and the fallback below handles the rest.
	/// </summary>
	private bool TryBindFormatterValueCall(CallExpressionSyntax call, SymbolTable scope, IReadOnlyList<TypeSymbol> argTypes)
	{
		if (call.Arguments.Count != 1 || argTypes.Count == 0)
			return false;

		if (!Formatting.TryClassifyContext(call, scope, out var kind) || kind != FormattingContextKind.FormatterWrite)
			return false;

		var argumentType = argTypes[0];
		if (ReferenceEquals(argumentType, OverloadResolver.DeferredCallableArgument))
			return false;

		var operation = Formatting.TryBind(argumentType, scope);
		if (operation is null)
		{
			Formatting.ReportTypeIsNotFormattable(call, argumentType, call.ArgumentListSpan);
			return true;
		}

		context.ResolvedFormattingOperations[call] = operation with { Context = kind };
		return true;
	}

	/// <summary>
	/// §7.2 fallback hook at the head of the 'no overload' block: when ordinary resolution found no
	/// applicable overload for a formatting-context call, bind the canonical formatting operation.
	/// Returns whether the call was fully handled (bound, or reported "not formattable").
	/// </summary>
	private bool TryBindFormattingFallback(CallExpressionSyntax call, SymbolTable scope, IReadOnlyList<TypeSymbol> argTypes)
	{
		if (call.Arguments.Count != 1 || argTypes.Count == 0)
			return false;

		if (!Formatting.TryClassifyContext(call, scope, out var kind))
			return false;

		var argumentType = argTypes[0];
		if (ReferenceEquals(argumentType, OverloadResolver.DeferredCallableArgument)
			|| ReferenceEquals(argumentType, OverloadResolver.UnresolvedArgument))
		{
			return false;
		}

		var operation = Formatting.TryBind(argumentType, scope);
		if (operation is null)
		{
			Formatting.ReportTypeIsNotFormattable(call, argumentType, call.ArgumentListSpan);
			return true;
		}

		context.ResolvedFormattingOperations[call] = operation with { Context = kind };
		return true;
	}
	/// reported. A leading-dot associated function is receiverless, so it can only be reached through
	/// its owner type; a receiver-backed method is the mirror image.
	/// </summary>
	/// <param name="ownerName">
	/// Owner type resolved for the mismatched call, preferred over the receiver text so a
	/// value-receiver call still names the type the associated function belongs to.
	/// </param>
	private bool ReportDottedCallMismatch(CallExpressionSyntax call, DottedCallMismatch mismatch, string? ownerName)
	{
		var lastDot = LastTopLevelDotIndex(call.FunctionName);
		if (lastDot <= 0 || lastDot == call.FunctionName.Length - 1)
			return false;

		var writtenOwner = call.FunctionName[..lastDot];
		var memberName = call.FunctionName[(lastDot + 1)..];
		var qualifiedOwner = $"{ownerName ?? writtenOwner}.{memberName}";

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		switch (mismatch)
		{
			case DottedCallMismatch.AssociatedCalledThroughValue:
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Associated function `{qualifiedOwner}` must be called through the type.",
					DiagnosticIds.AssociatedFunctionCalledThroughValue);
				return true;

			case DottedCallMismatch.InstanceCalledThroughType:
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"'{memberName}' is an instance method of '{ownerName ?? writtenOwner}' and must be called on a value, not through the type.",
					DiagnosticIds.InstanceExtensionCalledThroughType);
				return true;

			default:
				return false;
		}
	}

	/// <summary>
	/// Reports a dotted call whose receiver names neither a type nor a value, and returns whether one
	/// was reported. The member name behind an unresolvable receiver cannot be resolved, matched, or
	/// argued about, so continuing into overload resolution produces an argument-mismatch diagnostic
	/// against candidates that were never candidates. The receiver is the finding; the stable
	/// catalog entry for "this type name is not a type" is reused so no new identifier is minted.
	/// </summary>
	private bool ReportUnresolvedCallReceiver(SymbolTable scope, CallExpressionSyntax call)
	{
		var lastDot = LastTopLevelDotIndex(call.FunctionName);
		if (lastDot <= 0 || lastDot == call.FunctionName.Length - 1)
			return false;

		var receiver = call.FunctionName[..lastDot];

		// A receiver the type namespace can name is a type, and the ordinary member path reports
		// whatever is actually wrong with the member.
		if (context.ResolveType(receiver) is not null)
			return false;

		// A receiver that names a namespace is not missing either: `Console.Write()` is a namespace
		// member that does not exist, not a type that does not exist. Reporting it as an unknown type
		// would blame the wrong thing and change an established diagnostic for a common mistake.
		if (NamesNamespace(receiver))
			return false;

		// Any segment naming a value makes the receiver a value chain (`this.field`, `a.b.c`).
		// Its members are found by instance lookup, so a failure there is a different finding.
		if (receiver.Split('.').Any(segment =>
				segment is "self" or "this"
				|| scope.Lookup(segment) is VariableSymbol
				|| context.ResolveGlobalReference(segment, out _) is VariableSymbol))
		{
			return false;
		}

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(
			currentFileContext,
			call.ArgumentListSpan,
			$"Unknown type '{receiver}' in call to '{call.FunctionName}'",
			DiagnosticIds.UnknownTypeInExpression);
		return true;
	}

	/// <summary>
	/// Reports a dotted call whose receiver names a real struct or union type that has no such member,
	/// and returns whether one was reported. Overload resolution has already failed at this point, so
	/// the member genuinely does not exist rather than merely being called the wrong way. Reporting the
	/// member keeps the finding on the line that names it instead of emitting an argument-mismatch
	/// against a fabricated candidate set. Existing catalog entries are reused for the two aggregate
	/// kinds, so no new identifier is minted.
	/// </summary>
	private bool ReportMissingMemberOnKnownReceiver(CallExpressionSyntax call)
	{
		var lastDot = LastTopLevelDotIndex(call.FunctionName);
		if (lastDot <= 0 || lastDot == call.FunctionName.Length - 1)
			return false;

		var receiver = call.FunctionName[..lastDot];
		var memberName = call.FunctionName[(lastDot + 1)..];

		// A namespace-qualified receiver (e.g. `System.Console.Write`) is not a type name; a
		// type-qualified receiver (e.g. `Outer.Inner.Method`) is not this finding either.
		if (NamesNamespace(receiver))
			return false;

		if (context.ResolveType(receiver) is not TypeSymbol owner || owner is not (StructTypeSymbol or UnionTypeSymbol))
			return false;

		// A member that exists in any callable table is not missing; it simply did not match these
		// arguments, which the ordinary overload diagnostic already explains.
		if (HasCallableMember(memberName))
			return false;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		var isUnion = owner is UnionTypeSymbol;
		var noun = isUnion ? "variant" : "field";
		context.Diagnostics.Report(
			currentFileContext,
			call.ArgumentListSpan,
			$"Type '{owner.Name}' does not contain {noun} '{memberName}'",
			isUnion ? DiagnosticIds.UnionDoesNotContainVariant : DiagnosticIds.StructDoesNotContainField);
		return true;
	}

	/// <summary>
	/// Whether any callable with the given leaf name is known to the compiler, in any receiver form.
	/// Deliberately over-inclusive: a false positive here only suppresses a missing-member report,
	/// whereas a false negative would replace a legitimate overload diagnostic with the wrong finding.
	/// </summary>
	private bool HasCallableMember(string memberName)
	{
		static bool HasLeaf(IEnumerable<string> keys, string leaf)
		{
			foreach (var key in keys)
			{
				var lastDot = key.LastIndexOf('.');
				var keyLeaf = lastDot < 0 ? key : key[(lastDot + 1)..];
				if (string.Equals(keyLeaf, leaf, StringComparison.Ordinal))
					return true;
			}

			return false;
		}

		return HasLeaf(context.OverloadedFunctions.Keys, memberName)
			|| HasLeaf(context.AssociatedFunctions.Keys, memberName)
			|| HasLeaf(context.GenericFunctionTemplates.Keys, memberName)
			|| HasLeaf(context.GenericExtensionMethodTemplates.Keys, memberName)
			|| HasLeaf(context.InterfaceFunctionTemplates.Keys, memberName)
			|| HasLeaf(context.ProtocolFunctionTemplates.Keys, memberName);
	}

	/// <summary>
	/// Whether <paramref name="receiver"/> names a namespace rather than a type, either as written,
	/// through one of the file's active imports, or through a namespace alias. Uses the compiler's
	/// own symbol tables, so it agrees with what completion offers at the same position.
	/// </summary>
	private bool NamesNamespace(string receiver)
	{
		// 'using Native = OpenGL.Native;' makes the alias spelling itself name a namespace, so
		// Native.Callback(...) is a qualified value path and never a type-prefix diagnostic.
		if (context.GetNamespaceAliases(context.CurrentUnit).ContainsKey(receiver))
			return true;

		if (IsNamespaceName(receiver))
			return true;

		foreach (var active in context.GetActiveUsings(context.CurrentUnit))
		{
			if (IsNamespaceName(active + "." + receiver))
				return true;
		}

		return false;
	}

	private bool IsNamespaceName(string candidate)
	{
		if (context.DeclaredNamespaces.Contains(candidate))
			return true;

		var prefix = candidate + ".";
		return context.OverloadedFunctions.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.AssociatedFunctions.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.GenericFunctionTemplates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.GenericExtensionMethodTemplates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.InterfaceFunctionTemplates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.ProtocolFunctionTemplates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.StructTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.UnionTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.EnumTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.InterfaceTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| context.ProtocolTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
	}

	/// <summary>
	/// Finds the last '.' of a dotted call path that is not nested inside generic argument brackets, so a
	/// generic owner written as <c>Result&lt;int, Ns.MyError&gt;.Ok</c> keeps its inner qualified type names.
	/// </summary>
	private static int LastTopLevelDotIndex(string name)
	{
		var genericDepth = 0;
		var lastTopLevelDot = -1;

		for (var i = 0; i < name.Length; i++)
		{
			switch (name[i])
			{
				case '<':
					genericDepth++;
					break;
				case '>':
					if (genericDepth > 0)
						genericDepth--;
					break;
				case '.' when genericDepth == 0:
					lastTopLevelDot = i;
					break;
			}
		}

		return lastTopLevelDot;
	}

	/// <summary>
	/// Instantiates a generic extension member reached through its owner, such as
	/// <c>Layout.FromType&lt;MyStruct&gt;()</c>, <c>Pair&lt;int, Error&gt;.Ok(42)</c>, or
	/// <c>block.Scale&lt;int&gt;(3)</c>. The owner is resolved semantically, never assembled by string
	/// concatenation, and the type arguments are substituted into the signature before any overload
	/// validation runs; the resulting candidate is then an ordinary associated or instance symbol in the
	/// owner's overload table. A value receiver selects the receiver-backed form, a type receiver the
	/// receiverless one, matching the language's dispatch rule.
	/// </summary>
	/// <returns>
	/// Null when the callee is not a generic extension member, so the caller keeps its other generic-call
	/// paths. False when the callee was recognized but no concrete candidate should be materialized
	/// (invalid arguments, or an open TypeParameterSymbol argument while binding a template). True when
	/// a concrete candidate was registered and the call should be resolved again.
	/// </returns>
	private bool? TryInstantiateGenericExtensionMember(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope)
	{
		if (!Overloads.TrySplitDottedReceiver(call.FunctionName, scope, out var receiverIsValue, out var receiverType, out var memberName))
			return null;

		TypeSymbol? ownerType;
		if (receiverIsValue)
		{
			ownerType = receiverType;
			if (ownerType is PointerTypeSymbol receiverPointer)
				ownerType = receiverPointer.ReferencedType;

			if (ownerType is null)
				return null;
		}
		else if (!context.TrySplitTypeQualifiedName(call.FunctionName, context.CurrentUnit, out var ownerTypeName, out _)
			|| !context.TryResolveTypeByLookupName(ownerTypeName, context.CurrentUnit, out ownerType))
		{
			return null;
		}

		// A value receiver can only reach a receiver-backed member, and a type receiver only a
		// receiverless one; the wrong form is reported by the ordinary resolution that follows.
		var templates = context.GetGenericExtensionMethodTemplates(ownerType, context.CurrentUnit, memberName)
			.Where(t => t.Method.IsAssociated != receiverIsValue)
			.ToList();

		if (templates.Count == 0)
			return null;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		var template = templates.FirstOrDefault(t => t.Method.GenericParameters.Count == call.TypeArguments.Count);
		if (template is null)
		{
			var expected = templates.Min(t => t.Method.GenericParameters.Count);
			var expectedArgs = expected == 1 ? "argument" : "arguments";
			context.Diagnostics.Report(currentFileContext, call.Span,
				$"Function '{call.FunctionName}' expects {expected} type {expectedArgs} but received {call.TypeArguments.Count}", DiagnosticIds.FunctionExpectsTypeButReceived);
			return false;
		}

		var typeArgs = new List<TypeSymbol>(call.TypeArguments.Count);
		foreach (var writtenTypeArg in call.TypeArguments)
		{
			if (context.ResolveType(writtenTypeArg) is not TypeSymbol resolvedTypeArg)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Unknown type '{writtenTypeArg}' in type argument list of '{call.FunctionName}'", DiagnosticIds.UnknownTypeInTypeArgumentListOf);
				return false;
			}

			typeArgs.Add(resolvedTypeArg);
		}

		// Template binding is not monomorphization. `Layout.FromType<T>()` inside an open generic
		// declaration may be checked while T is a TypeParameterSymbol, but registering a synthetic
		// `FromType<T>` declaration would enqueue its body for validation after the generic scope has
		// been popped. That poisoned the validation queue with false CVL2103/CVL2104 diagnostics from
		// `sizeof<T>()` / `alignof<T>()`. Keep the call deferred until a concrete call-site supplies
		// concrete type arguments; generic-scope validation intentionally suppresses the unresolved
		// concrete-overload diagnostic for that open call.
		if (typeArgs.Any(ContainsTypeParameter))
			return false;

		if (Generics.InstantiateGenericExtensionMethod(template, typeArgs) is null)
		{
			context.Diagnostics.Report(currentFileContext, call.Span,
				$"Cannot resolve the signature of '{call.FunctionName}' for the given type arguments", DiagnosticIds.CannotResolveTheSignatureOfForTheGivenTypeArguments);
			return false;
		}

		return true;
	}

	/// <summary>
	/// Applies omitted-argument type inference to a top-level generic function call
	/// (Inc - Generic Constraint Semantic Parity §10/§11). Type arguments are unified positionally
	/// from the concrete argument types; the substituted constraints are evaluated without
	/// reporting. A satisfied candidate is instantiated and returned; a failed candidate is
	/// non-applicable (the failure is surfaced as the primary diagnostic only when nothing else
	/// resolves); an uninferrable signature — composed parameter types, wrapped refs without a
	/// pointer argument, conflicting or unfillable positions — falls through to ordinary
	/// no-overload reporting.
	/// </summary>
	private FunctionSymbol? TryInferTopLevelGenericFunction(
		CallExpressionSyntax call,
		IReadOnlyList<TypeSymbol> argTypes,
		SymbolTable scope,
		out BindingContext.GenericConstraintEvaluation? constraintFailure)
	{
		constraintFailure = null;

		var templateName = Generics.ResolveFunctionTemplateName(call.FunctionName, scope);
		if (templateName is null || !context.GenericFunctionTemplates.TryGetValue(templateName, out var templateDecl))
			return null;

		if (templateDecl.Parameters.Count != argTypes.Count)
			return null;

		var genericParams = templateDecl.GenericParameters.ToList();
		var inferred = new TypeSymbol?[genericParams.Count];
		for (var i = 0; i < templateDecl.Parameters.Count; i++)
		{
			var typeText = templateDecl.Parameters[i].Type;
			TypeSymbol effectiveArg = argTypes[i];
			if (typeText.StartsWith("refvar ", StringComparison.Ordinal) || typeText.StartsWith("ref ", StringComparison.Ordinal))
			{
				if (argTypes[i] is not PointerTypeSymbol pointer)
					return null;
				effectiveArg = pointer.ReferencedType;
				typeText = typeText.StartsWith("refvar ", StringComparison.Ordinal) ? typeText[7..] : typeText[4..];
			}

			var paramIndex = genericParams.IndexOf(typeText);
			if (paramIndex < 0)
			{
				// Composed parameter types (`List<T>`, `T[]`, ...) are not decomposed by inference;
				// the call falls through to the ordinary finding unless every position is inferable
				// from a direct generic parameter (the INC's §10/§11 examples).
				return null;
			}

			if (inferred[paramIndex] is { } existing && !existing.Equals(effectiveArg))
				return null;
			inferred[paramIndex] = effectiveArg;
		}

		for (var i = 0; i < inferred.Length; i++)
		{
			if (inferred[i] is null)
				return null;
		}

		var typeArgs = inferred.Select(static t => t!).ToList();
		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < genericParams.Count; i++)
			substitutionMap[genericParams[i]] = typeArgs[i];

		foreach (var (paramName, requiredContracts) in templateDecl.GenericParameterConstraints)
		{
			var paramIndex = genericParams.IndexOf(paramName);
			if (paramIndex < 0)
				continue;

			foreach (var contractText in requiredContracts)
			{
				var result = context.EvaluateConstraint(paramName, contractText, typeArgs[paramIndex], substitutionMap);
				if (!result.Passed)
				{
					constraintFailure = result;
					return null;
				}
			}
		}

		return Generics.InstantiateGenericFunction(templateDecl, typeArgs, scope);
	}

	/// <summary>
	/// Resolves a call whose generic extension member was just instantiated, reusing the ordinary
	/// overload path so receiver form, receiver conversion, and the wrong-receiver-form diagnostics
	/// behave exactly as they do for a non-generic member.
	/// </summary>
	private FunctionSymbol? ResolveInstantiatedExtensionMember(
		CallExpressionSyntax call,
		IReadOnlyList<TypeSymbol> argTypes,
		SymbolTable scope,
		out DottedCallMismatch mismatch,
		out string? mismatchOwnerName)
		=> Calls.ResolveOrdinaryCall(call, argTypes, scope, out mismatch, out mismatchOwnerName);

	/// <summary>
	/// Validates member access and returns the resolved semantic member type when available.
	/// </summary>
	/// <param name="expr">The member access expression.</param>
	/// <param name="scope">The scope the expression is evaluated in.</param>
	/// <param name="expectedType">
	/// The surrounding target type, consulted only for the context-typed <c>Option.None</c> form.
	/// </param>
	private TypeSymbol? CheckMemberAccessExpression(MemberAccessExpressionSyntax expr, SymbolTable scope, TypeSymbol? expectedType = null)
	{
		// Context-typed `Option.None`. The bare `Option` template has no payload type of its own, so
		// the surrounding target type is the only thing that can supply `T`. Like the enum path below,
		// the receiver is a *type name* here, so this must resolve before the receiver is checked as a
		// value expression or `Option` would be reported as an undefined variable.
		if (TryResolveOptionTemplateReceiver(expr) is { } optionTemplate)
		{
			return ResolveTargetTypedOptionNone(expr, optionTemplate, expectedType);
		}

		// Namespace-qualified global access: Ns.Sub.Member. Resolved before the receiver is
		// checked as an expression so 'Math' is not reported as an undefined variable.
		if (TryResolveNamespaceGlobal(expr, scope, out var globalSymbol))
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
					$"Enum '{enumType.Name}' does not contain variant '{expr.MemberName}'", DiagnosticIds.EnumDoesNotContainVariant);
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
				$"Type '{leftType.Name}' is an enum; only scoped variant access ('{leftType.Name}.VariantName') is allowed.", DiagnosticIds.TypeIsAnEnumOnlyScopedVariantAccessVariantNameIsAllowed);
			return null;
		}

		if (leftType is UnionTypeSymbol unionType)
		{
			// Raw-union projection is a reinterpretation operation.  Validate it here, after
			// ordinary member/namespace resolution has already produced the receiver type.
			// Do not re-run GetType(expr.Expression): namespace-qualified expressions are not
			// value receivers and a second generic lookup would spuriously diagnose their root
			// namespace (for example `System`) as an undefined variable.
			if (unionType.IsUnsafe)
			{
				_validation.RequireUnsafe(
					context,
					expr,
					UnsafeUsageKind.UnsafeUnionAccess,
					$"Accessing field '{expr.MemberName}' of raw unsafe union '{unionType.Name}' requires an unsafe context.",
					DiagnosticIds.UnsafeUnionAccessRequiresUnsafe);
			}

			var variantField = unionType.FindField(expr.MemberName);
			if (variantField is null)
			{
				if (context.TryFindInvalidAggregateMember(unionType.Name, expr.MemberName, isUnion: true, out _))
					return null;
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span, $"Union '{unionType.Name}' does not contain variant '{expr.MemberName}'", DiagnosticIds.UnionDoesNotContainVariant);
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
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Type '{leftType.Name}' is not a struct or union; cannot access member '{expr.MemberName}'", DiagnosticIds.TypeIsNotAStructOrUnionCannotAccessMember);
			return null;
		}

		var field = structType.FindField(expr.MemberName);
		if (field is null)
		{
			if (context.TryFindInvalidAggregateMember(structType.Name, expr.MemberName, isUnion: false, out _))
				return null;
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Struct '{structType.Name}' does not contain field '{expr.MemberName}'", DiagnosticIds.StructDoesNotContainField);
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
	private bool TryResolveNamespaceGlobal(MemberAccessExpressionSyntax expr, SymbolTable? scope, out VariableSymbol? globalSymbol)
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

		globalSymbol = context.ResolveQualifiedGlobal(string.Join(".", segments), expr.MemberName, scope);
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
	/// Resolves the receiver of a context-typed <c>Option.None</c> when it is the Option template
	/// used as a type name. Returns the resolved union when <paramref name="m"/> is the
	/// context-typed none-state constructor, and null otherwise.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The two-token surface shape <c>Option.None</c> is what makes this a language form rather than
	/// an ordinary member access, so the name is matched here exactly as the optional-syntax rewriter
	/// matches it. Nothing is bound to the Option type itself: the payload type comes from the
	/// surrounding target type at each use site.
	/// </para>
	/// <para>
	/// The structural Option check is applied only when the template's variants are populated. An
	/// unresolved generic template has no payload type to inspect, so demanding the full shape there
	/// would reject every use of the form.
	/// </para>
	/// </remarks>
	private UnionTypeSymbol? TryResolveOptionTemplateReceiver(MemberAccessExpressionSyntax m)
	{
		var dotted = GetDottedName(m.Expression);
		if (dotted is null || m.MemberName != "None")
			return null;

		if (dotted != "Option" && !dotted.EndsWith(".Option", StringComparison.Ordinal))
			return null;

		if (context.ResolveType(dotted) is not UnionTypeSymbol optionUnion)
			return null;

		// A union that borrowed the Option name but does not have the Option shape is not
		// constructible through this form.
		if (optionUnion.NoneVariant is not null && !optionUnion.IsOption)
			return null;

		return optionUnion;
	}

	/// <summary>
	/// Types a context-typed <c>Option.None</c> against the surrounding target type, or reports that no
	/// target type is available to supply the payload type.
	/// </summary>
	/// <remarks>
	/// The verdict is cached per expression node, including the diagnosed failure. <see cref="GetType"/>
	/// re-runs member-access validation, so without the cache a failing <c>Option.None</c> would be
	/// diagnosed once per query, and a later query that carries no target type would silently discard
	/// an earlier successful binding.
	/// </remarks>
	private TypeSymbol? ResolveTargetTypedOptionNone(
		MemberAccessExpressionSyntax expr,
		UnionTypeSymbol optionTemplate,
		TypeSymbol? expectedType)
	{
		if (context.ResolvedTargetTypedOptionNone.TryGetValue(expr, out var cached))
			return cached;

		if (expectedType is not UnionTypeSymbol { IsOption: true } targetOption)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span,
				$"'Option.{expr.MemberName}' requires a target Option<T> type to determine the payload type. " +
				"Declare the target as 'T?' or 'Option<T>', or name the type explicitly.",
				DiagnosticIds.OptionNoneRequiresTargetType);

			context.ResolvedTargetTypedOptionNone.Add(expr, null);
			return null;
		}

		// The payload types must agree: a target Option<long> cannot accept an Option.None that the
		// surrounding context reads as Option<long> only if they are the same type, which the target
		// type already guarantees by being the single source of truth here.
		context.ResolvedTargetTypedOptionNone.Add(expr, targetOption);
		return targetOption;
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
			tgtPtr.ReferencedType.Equals(srcPtr.ReferencedType))
			return true;

		return false;
	}

	/// <summary>
	/// Classifies <c>T*[index]</c> as a raw-pointer storage access. Raw-pointer indexing is a
	/// genuine unsafe operation in its own right, so it is recorded through the canonical
	/// <see cref="ValidationContext.RequireUnsafe"/> path whether the value is read, written, or
	/// addressed. It deliberately carries no bounds check and no null check: it is unchecked C-style
	/// pointer arithmetic, and an invalid resulting address stays ordinary unsafe raw-pointer
	/// behavior.
	/// </summary>
	private void CheckRawPointerIndex(IndexExpressionSyntax index, SymbolTable scope)
	{
		if (GetType(index.Left, scope) is not RawPointerTypeSymbol rawPointer)
			return;

		_validation.RequireUnsafe(
			context,
			index,
			UnsafeUsageKind.RawPointerIndex,
			"Indexing a raw pointer requires an unsafe context.",
			DiagnosticIds.RawPointerIndexRequiresUnsafe);

		if (!RawPointerPointeeIsIndexable(rawPointer.ElementType))
		{
			context.Diagnostics.Report(
				context.FileContexts[context.CurrentUnit!],
				index.Left.Span,
				$"Raw-pointer indexing requires an indexable pointee type, but '{rawPointer.ElementType.Name}' has no element stride.",
				DiagnosticIds.RawPointerIndexPointeeIsNotIndexable);
			return;
		}

		// The index operand is source-language semantics, not an LLVM GEP detail. Reuse the
		// canonical integer domain that raw-pointer arithmetic already accepts for `ptr + index`
		// so this INC introduces no new implicit index conversions.
		var indexType = GetType(index.Index, scope);
		if (indexType is not null && !TypeSymbol.IsIntegerType(indexType))
		{
			context.Diagnostics.Report(
				context.FileContexts[context.CurrentUnit!],
				index.Index.Span,
				$"Raw-pointer index must be an integer, but it is '{indexType.Name}'.",
				DiagnosticIds.RawPointerIndexMustBeAnInteger);
		}
	}

	/// <summary>
	/// Raw-pointer indexing is available only where raw-pointer stepping already accepts the
	/// pointee, because both forms need the same concrete element stride. An opaque or unsized
	/// pointee such as <c>void</c> stays non-indexable rather than becoming indexable here.
	/// </summary>
	private static bool RawPointerPointeeIsIndexable(TypeSymbol? elementType) =>
		elementType is not null && !elementType.Equals(TypeSymbol.Void);

	/// <summary>
	/// Validates named struct or union initialization and returns the constructed semantic type.
	/// </summary>
	private readonly Dictionary<(StructInitializationExpressionSyntax Syntax, SymbolTable Scope, CompilationUnitSyntax? Unit, string ConstructedName), TypeSymbol?> _resolvedInitializers = [];

	private TypeSymbol? GetOrBindStructInitializer(StructInitializationExpressionSyntax expr, SymbolTable scope)
	{
		// Avoid resolving the type a second time during a type query. The cache is scoped
		// to the validator/binding snapshot, syntax occurrence, lexical scope and type.
		var key = (expr, scope, context.CurrentUnit, expr.StructTypeName);
		if (_resolvedInitializers.TryGetValue(key, out var result))
			return result;

		var type = context.ResolveType(expr.StructTypeName);
		result = BindStructInitializer(expr, scope, type);
		_resolvedInitializers[key] = result; // Even null (failed bind) is cached.
		return result;
	}

	private TypeSymbol? BindStructInitializer(StructInitializationExpressionSyntax expr, SymbolTable scope, TypeSymbol? type)
	{
		if (type is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Unknown type '{expr.StructTypeName}'", DiagnosticIds.UnknownType);
			return null;
		}

		// Bare generic declarations are not concrete types. Preserve the existing
		// explicit-default rule, but do not infer type arguments from field values.
		if (type is StructTypeSymbol or UnionTypeSymbol && !expr.StructTypeName.Contains('<'))
		{
			IReadOnlyList<string>? defaultArguments = null;
			if (context.GenericStructTemplates.TryGetValue(type.Name, out var structTemplate))
			{
				if (structTemplate.GenericParameters.All(p => structTemplate.GenericParameterDefaults.ContainsKey(p)))
					defaultArguments = structTemplate.GenericParameters.Select(p => structTemplate.GenericParameterDefaults[p]).ToList();
			}
			else if (context.GenericUnionTemplates.TryGetValue(type.Name, out var unionTemplate))
			{
				if (unionTemplate.GenericParameters.All(p => unionTemplate.GenericParameterDefaults.ContainsKey(p)))
					defaultArguments = unionTemplate.GenericParameters.Select(p => unionTemplate.GenericParameterDefaults[p]).ToList();
			}
			else
				defaultArguments = [];

			if (defaultArguments is null)
			{
				context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], new TextSpan(expr.Span.Start, expr.StructTypeName.Length),
					$"Generic type '{expr.StructTypeName}' requires explicit type arguments.", DiagnosticIds.GenericInitializerRequiresTypeArguments);
				return null;
			}
			if (defaultArguments.Count > 0)
			{
				type = context.ResolveType($"{expr.StructTypeName}<{string.Join(", ", defaultArguments)}>");
				if (type is null)
					return null;
			}
		}

		if (type is UnionTypeSymbol unionType)
		{
			if (expr.Initializers.Count != 1)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span, $"Union initialization of '{unionType.Name}' must specify exactly one variant.", DiagnosticIds.UnionInitializationOfMustSpecifyExactlyOneVariant);
				return unionType;
			}

			var init = expr.Initializers[0];
			var field = unionType.FindField(init.MemberName);
			if (field is null)
			{
				if (context.TryFindInvalidAggregateMember(unionType.Name, init.MemberName, isUnion: true, out _))
					return null; // Declared, but invalid: original type declaration owns the error.
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span, $"Union '{unionType.Name}' does not contain variant '{init.MemberName}'", DiagnosticIds.UnionDoesNotContainVariant);
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
			else if (field.Type is DelegateTypeSymbol delegateFieldType)
			{
				CheckDelegateValue(init.Expression, delegateFieldType, scope);
			}
			else
			{
				Check(init.Expression, scope);
			}

			var initType = field.Type is DelegateTypeSymbol ? null : GetType(init.Expression, scope);
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
						context.Diagnostics.Report(currentFileContext, init.Span, "The 'null' literal requires a pointer type (Option or raw pointer).", DiagnosticIds.TheNullLiteralRequiresAPointerTypeOptionOrRawPointer);
					}
					else
					{
						context.Diagnostics.Report(currentFileContext, init.Span, $"Cannot initialize field '{init.MemberName}' of type '{field.Type.Name}' with value of type '{initType.Name}'", DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
					}
				}
			}

			return context.HasInvalidAggregateMembers(unionType.Name, isUnion: true) ? null : unionType;
		}

		if (type is not StructTypeSymbol structType)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Type '{expr.StructTypeName}' is not a struct type", DiagnosticIds.TypeIsNotAStructType);
			return null;
		}

		var initializedFields = new HashSet<string>();
		foreach (var init in expr.Initializers)
		{
			if (!initializedFields.Add(init.MemberName))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span, $"Duplicate initializer for field '{init.MemberName}'", DiagnosticIds.DuplicateInitializerForField);
				continue;
			}

			var field = structType.FindField(init.MemberName);
			if (field is null)
			{
				if (context.TryFindInvalidAggregateMember(structType.Name, init.MemberName, isUnion: false, out _))
					continue; // Member exists; its unresolved type cannot be checked here.
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, init.Span, $"Struct '{structType.Name}' does not contain field '{init.MemberName}'", DiagnosticIds.StructDoesNotContainField);
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
				context.Diagnostics.Report(currentFileContext, init.Span, $"Cannot initialize field '{init.MemberName}' of type '{field.Type.Name}' with value of type '{initType.Name}'", DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
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
				context.Diagnostics.Report(currentFileContext, expr.Span, $"Missing initializer for field '{field.Name}' of struct '{structType.Name}'", DiagnosticIds.MissingInitializerForFieldOfStruct);
			}
		}

		return context.HasInvalidAggregateMembers(structType.Name, isUnion: false) ? null : structType;
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
				context.Diagnostics.Report(currentFileContext, expr.Elements[i].Span, $"Array elements must have the same type. Expected '{elementType.Name}', found '{elType.Name}'", DiagnosticIds.ArrayElementsMustHaveTheSameTypeExpectedFound);
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
			context.Diagnostics.Report(currentFileContext, expr.Span, "Cannot take a mutable reference (refvar) of a read-only variable.", DiagnosticIds.CannotTakeAMutableReferenceRefvarOfAReadOnlyVariable);
		}

		return new PointerTypeSymbol(innerType, expr.IsMutable);
	}

	/// <summary>
	/// Detects source-level `val` and readonly `ref` access paths without treating a
	/// legacy bare typed binding as deeply immutable. This intentionally does
	/// not reclassify slices or raw pointers in INC A.
	/// </summary>
	private bool IsExplicitReadOnlyPath(ExpressionSyntax expression, SymbolTable scope)
	{
		switch (expression)
		{
			case IdentifierExpressionSyntax identifier:
				{
					var symbol = scope.Lookup(identifier.Name) as VariableSymbol
						?? context.ResolveGlobalReference(identifier.Name, out _);
					if (symbol is null && identifier.Name == "this")
						symbol = scope.Lookup("this") as VariableSymbol;
					if (symbol is null)
						return false;
					return symbol.Type is PointerTypeSymbol reference
						? !reference.IsMutable : symbol.IsExplicitVal;
				}
			case MemberAccessExpressionSyntax member:
				return IsExplicitReadOnlyPath(member.Expression, scope);
			case IndexExpressionSyntax index:
				return IsExplicitReadOnlyPath(index.Left, scope);
			default:
				return false;
		}
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
			context.Diagnostics.Report(currentFileContext, expr.Condition.Span, $"Ternary condition must be 'bool', found '{condType.Name}'", DiagnosticIds.TernaryConditionMustBeBoolFound);
		}

		Check(expr.ThenExpression, scope);
		Check(expr.ElseExpression, scope);

		var thenType = GetType(expr.ThenExpression, scope);
		var elseType = GetType(expr.ElseExpression, scope);

		if (thenType is not null && elseType is not null && !thenType.Equals(elseType))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, expr.Span, $"Ternary branches must have the same type. Found '{thenType.Name}' and '{elseType.Name}'", DiagnosticIds.TernaryBranchesMustHaveTheSameTypeFoundAnd);
		}

		return thenType;
	}

	/// <summary>
	/// Validates an Option fallback expression and returns its canonical semantic descriptor.
	/// </summary>
	/// <remarks>
	/// Both operands are always checked, so an error confined to the right operand is still reported
	/// even though the right operand's code is only emitted on the runtime <c>None</c> path.
	/// </remarks>
	/// <param name="coalesce">The fallback expression.</param>
	/// <param name="scope">The scope the expression is evaluated in.</param>
	/// <param name="expectedType">
	/// The surrounding target type, forwarded to the left operand only.
	/// </param>
	/// <remarks>
	/// The operator stays strictly left-driven: the left operand must resolve to an <c>Option&lt;T&gt;</c>
	/// on its own before the right operand is considered. That is what keeps
	/// <c>val x = Option.None ?? 123;</c> an error — with no target type there is nothing to type the
	/// left operand with, and the payload type must not be back-inferred from the right operand. The
	/// target type is still allowed to flow in, because <c>int? b = Option.None ?? 123;</c> gets its
	/// <c>Option&lt;int&gt;</c> from the declaration, not from the fallback.
	/// </remarks>
	private ResolvedCoalesceExpression GetOrBindCoalesceExpression(
		CoalesceExpressionSyntax coalesce,
		SymbolTable scope,
		TypeSymbol? expectedType = null)
	{
		if (context.ResolvedCoalesceExpressions.TryGetValue(coalesce, out var existing))
			return existing;

		// The left operand is always evaluated, so it is checked first. The right operand is checked
		// unconditionally too: short-circuiting defers its code emission to the None branch, not its
		// validation.
		Check(coalesce.Left, scope, expectedType);
		Check(coalesce.Right, scope);

		var leftType = GetType(coalesce.Left, scope, expectedType);
		var rightType = GetType(coalesce.Right, scope);
		var resolved = CoalesceExpressionBinder.Bind(coalesce, leftType, rightType, expectedType);

		// An operand typing that is merely incomplete is not a verdict. Validation continues with a
		// non-cached descriptor so a later, better-informed query can resolve it.
		if (resolved.ErrorKind == CoalesceBindingErrorKind.UnresolvedOperand)
			return resolved;

		context.ResolvedCoalesceExpressions.Add(coalesce, resolved);

		if (!resolved.IsSuccess)
			ReportCoalesceBindingError(resolved);

		return resolved;
	}

	/// <summary>Reports the primary diagnostic retained by an error-bound fallback operation.</summary>
	private void ReportCoalesceBindingError(ResolvedCoalesceExpression resolved)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		switch (resolved.ErrorKind)
		{
			case CoalesceBindingErrorKind.LeftOperandNotOption:
				context.Diagnostics.Report(
					currentFileContext,
					resolved.Syntax.Left.Span,
					$"Coalescing operator requires Option<T> on the left operand; found '{resolved.LeftType?.Name ?? "unknown"}'",
					DiagnosticIds.CoalesceLeftOperandMustBeOptionFound);
				break;

			case CoalesceBindingErrorKind.RightOperandNotCompatible:
				context.Diagnostics.Report(
					currentFileContext,
					resolved.Syntax.Right.Span,
					$"Right operand is not compatible with coalescing payload type '{resolved.PayloadType?.Name ?? resolved.OptionType?.PayloadVariant?.Type.Name ?? "unknown"}'",
					DiagnosticIds.CoalesceRightOperandNotCompatibleWithPayloadTypeOf);
				break;

			case CoalesceBindingErrorKind.UnresolvedOperand:
			case null:
				break;
		}
	}

	/// <summary>
	/// Binds an optional-chaining expression, caching the resolved descriptor on success.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The receiver is always checked, and so are the arguments: skipping an argument because the
	/// receiver may be absent would leave it unvalidated. Only evaluation is conditional, never
	/// binding (<c>bind always, evaluate conditionally</c>).
	/// </para>
	/// <para>
	/// The member is resolved against the Option <em>payload</em> type, never the Option itself, so
	/// <c>handler?.Invoke(42)</c> finds the synthesized <c>Delegate.Invoke</c> of the delegate type
	/// stored inside the Option.
	/// </para>
	/// </remarks>
	private ResolvedOptionalChainExpression GetOrBindOptionalChain(OptionalChainExpressionSyntax chain, SymbolTable scope)
	{
		if (context.ResolvedOptionalChains.TryGetValue(chain, out var cached))
			return cached;

		Check(chain.Receiver, scope);

		var argumentTypes = new List<TypeSymbol>();
		if (chain.Arguments is not null)
		{
			foreach (var argument in chain.Arguments)
			{
				Check(argument, scope);
				argumentTypes.Add(GetType(argument, scope) ?? TypeSymbol.Int);
			}
		}

		var receiverType = GetType(chain.Receiver, scope);
		if (receiverType is null)
			return ResolvedOptionalChainExpression.Error(chain, OptionalChainBindingErrorKind.UnresolvedOperand, null, null);

		if (receiverType is PointerTypeSymbol receiverPointer)
			receiverType = receiverPointer.ReferencedType;

		// `?.` is Option semantics, not null magic: a plain value has no absence to propagate.
		if (receiverType is not UnionTypeSymbol { IsOption: true } optionType || optionType.PayloadVariant is not { } payload)
		{
			return CacheOptionalChainError(chain, OptionalChainBindingErrorKind.ReceiverNotOption, receiverType, null);
		}

		var payloadType = payload.Type;
		if (payloadType is PointerTypeSymbol payloadPointer)
			payloadType = payloadPointer.ReferencedType;

		FunctionSymbol? method = null;
		DelegateTypeSymbol? delegateType = null;
		TypeSymbol? memberType = null;
		var memberKind = OptionalChainMemberKind.Field;

		if (chain.IsInvocation)
		{
			// `Invoke` on a delegate receiver is the compiler-owned semantic member synthesized from
			// the DelegateTypeSymbol. It is not a source declaration, not an extension method and not
			// an emitted wrapper, so it shares the ordinary delegate-call lowering verbatim.
			if (payloadType is DelegateTypeSymbol synthesizedDelegate && chain.MemberName == "Invoke")
			{
				delegateType = synthesizedDelegate;
				memberKind = OptionalChainMemberKind.DelegateInvoke;
				memberType = synthesizedDelegate.ReturnType;

				if (argumentTypes.Count != synthesizedDelegate.Parameters.Count)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(
						currentFileContext,
						chain.Span,
						$"Delegate '{synthesizedDelegate.Name}' expects {synthesizedDelegate.Parameters.Count} argument(s) but received {argumentTypes.Count}",
						DiagnosticIds.DelegateExpectsArgumentSButReceived);
				}
				else if (Overloads.CompareSignature(
					synthesizedDelegate.Parameters.Select(parameter => parameter.Type).ToArray(),
					argumentTypes,
					isVariadic: false) < 0)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					var actual = string.Join(", ", argumentTypes.Select(type => type.Name));
					context.Diagnostics.Report(
						currentFileContext,
						chain.Span,
						$"Delegate '{synthesizedDelegate.Name}' cannot be invoked with argument types ({actual}).",
						DiagnosticIds.DelegateCannotBeInvokedWithArgumentTypes);
				}

				// `?.` tests only the outer Option state; the inner native function pointer keeps its
				// existing unsafe requirement and receives no implicit null check.
				if (synthesizedDelegate.IsNative)
				{
					_validation.RequireUnsafe(
						context,
						chain,
						UnsafeUsageKind.NativeDelegateInvocation,
						$"Invoking native delegate '{synthesizedDelegate.Name}' requires an unsafe context.",
						DiagnosticIds.NativeDelegateInvokeRequiresUnsafe);
				}
			}
			else
			{
				// Resolve against the payload type directly rather than through a dotted source name:
				// there is no source-level `payload.Method` spelling to hand the overload resolver.
				var candidates = context
					.GetExtensionMethodCandidates(optionType.PayloadVariant!.Type, context.CurrentUnit, chain.MemberName)
					.Select(candidate => candidate.Function)
					.ToList();

				var bestMatch = (FunctionSymbol?)null;
				var bestScore = -1;
				foreach (var candidate in candidates)
				{
					// Instance extensions take the payload as an implicit leading `this` argument, so the
					// written argument types are scored one position later. Reusing the candidate's own
					// receiver parameter makes that argument match by symbol identity, which preserves the
					// `ref`/`refvar` mutability the declaration was registered with.
					var scoredArgumentTypes = new List<TypeSymbol>(argumentTypes.Count + 1) { candidate.Parameters[0].Type };
					scoredArgumentTypes.AddRange(argumentTypes);

					var parameterTypes = candidate.Parameters.Select(parameter => parameter.Type).ToList();
					var score = Overloads.CompareSignature(parameterTypes, scoredArgumentTypes, candidate.IsVariadic);
					if (score > bestScore)
					{
						bestScore = score;
						bestMatch = candidate;
					}
				}

				if (bestMatch is null)
					return CacheOptionalChainError(chain, OptionalChainBindingErrorKind.MemberNotFound, receiverType, payload.Type);

				method = bestMatch;
				memberKind = OptionalChainMemberKind.Method;
				memberType = bestMatch.ReturnType;
			}
		}
		else
		{
			var declaredPayload = optionType.PayloadVariant.Type;
			var fieldType = declaredPayload switch
			{
				StructTypeSymbol structType => structType.FindField(chain.MemberName)?.Type,
				UnionTypeSymbol unionType => unionType.FindField(chain.MemberName)?.Type,
				_ => null,
			};

			if (fieldType is null)
				return CacheOptionalChainError(chain, OptionalChainBindingErrorKind.MemberNotFound, receiverType, declaredPayload);

			memberType = fieldType;
		}

		// A `void` member produces no value at all: no Option<void> is synthesized, so the None
		// branch is a plain no-op.
		if (memberType is null || memberType.Equals(TypeSymbol.Void))
		{
			var voidResult = ResolvedOptionalChainExpression.Void(chain, optionType, receiverType, payload.Type, memberKind, method, delegateType);
			context.ResolvedOptionalChains[chain] = voidResult;
			return voidResult;
		}

		// LiftOptional adds exactly one synthetic Option layer: a member already returning Option<U>
		// keeps that single layer, and any other R becomes Option<R>.
		var resultOption = ResolveLiftedOption(memberType);
		if (resultOption is null)
			return CacheOptionalChainError(chain, OptionalChainBindingErrorKind.MemberNotFound, receiverType, payload.Type);

		var resolved = ResolvedOptionalChainExpression.Success(
			chain,
			optionType,
			receiverType,
			payload.Type,
			memberType,
			resultOption,
			memberKind,
			method,
			delegateType,
			lifts: true);
		context.ResolvedOptionalChains[chain] = resolved;
		return resolved;
	}

	/// <summary>
	/// Returns <c>Option&lt;R&gt;</c> for a lifted member result of type <paramref name="memberType"/>.
	/// </summary>
	/// <remarks>
	/// One synthetic layer is always added, so a member that already returns <c>Option&lt;U&gt;</c> is
	/// typed <c>Option&lt;Option&lt;U&gt;&gt;</c> rather than collapsing back to <c>Option&lt;U&gt;</c>: the
	/// chain's own presence and the member's presence are independent facts and both must survive.
	/// </remarks>
	private UnionTypeSymbol? ResolveLiftedOption(TypeSymbol memberType)
		=> context.ResolveType($"Option<{memberType.Name}>") as UnionTypeSymbol;

	private ResolvedOptionalChainExpression CacheOptionalChainError(
		OptionalChainExpressionSyntax chain,
		OptionalChainBindingErrorKind errorKind,
		TypeSymbol? receiverType,
		TypeSymbol? payloadType)
	{
		var error = ResolvedOptionalChainExpression.Error(chain, errorKind, receiverType, payloadType);
		context.ResolvedOptionalChains[chain] = error;
		ReportOptionalChainBindingError(error);
		return error;
	}

	private void ReportOptionalChainBindingError(ResolvedOptionalChainExpression resolved)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		switch (resolved.ErrorKind)
		{
			case OptionalChainBindingErrorKind.ReceiverNotOption:
				context.Diagnostics.Report(
					currentFileContext,
					resolved.Syntax.Receiver.Span,
					$"Optional chaining requires Option<T> as the receiver; found '{resolved.ReceiverType?.Name ?? "unknown"}'",
					DiagnosticIds.OptionalChainReceiverMustBeOptionFound);
				break;

			case OptionalChainBindingErrorKind.MemberNotFound:
				context.Diagnostics.Report(
					currentFileContext,
					resolved.Syntax.Span,
					$"Type '{resolved.PayloadType?.Name ?? "unknown"}' does not contain member '{resolved.Syntax.MemberName}'",
					resolved.Syntax.IsInvocation
						? DiagnosticIds.NoOverloadOfFunctionMatchesArgumentTypes
						: DiagnosticIds.OptionalChainPayloadDoesNotContainMember);
				break;

			case OptionalChainBindingErrorKind.UnresolvedOperand:
				break;
		}
	}

	/// <summary>
	/// Returns the final canonical binary binding for the current validation context, creating it
	/// exactly once after declaration registration is complete. Error bindings are cached as final
	/// results as well, so repeated type queries cannot repeat overload search or diagnostics.
	/// </summary>
	private ResolvedBinaryExpression GetOrBindBinaryExpression(BinaryExpressionSyntax binary, SymbolTable scope, bool final = false)
	{
		if (context.ResolvedBinaryExpressions.TryGetValue(binary, out var existing))
			return existing;

		var leftType = GetType(binary.Left, scope);
		var rightType = GetType(binary.Right, scope);
		var resolved = BinaryExpressionBinder.Bind(context, binary, leftType, rightType);

		// A type probe can happen before a child call/lambda has itself been bound. Such an
		// incomplete result is not canonical: caching it would poison later validation with a
		// transient Error. Final validation, on the other hand, records failure as well as success.
		if (!final && resolved.ErrorKind == BinaryBindingErrorKind.UnresolvedOperand)
			return resolved;

		context.ResolvedBinaryExpressions.Add(binary, resolved);

		if (resolved.State == BinaryBindingState.Error)
			ReportBinaryBindingError(resolved);

		return resolved;
	}

	/// <summary>Reports the one primary diagnostic retained by a final error-bound binary operation.</summary>
	private void ReportBinaryBindingError(ResolvedBinaryExpression resolved)
	{
		switch (resolved.ErrorKind)
		{
			case BinaryBindingErrorKind.None:
			case BinaryBindingErrorKind.UnresolvedOperand:
				return;
			case BinaryBindingErrorKind.EnumIntegerMix:
				CheckEnumIntMismatch(resolved.LeftType, resolved.RightType, resolved.Syntax.Span);
				return;
		}

		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		if (resolved.ErrorKind == BinaryBindingErrorKind.DynamicStringConcatenation)
		{
			context.Diagnostics.Report(currentFileContext, resolved.Syntax.Span,
				"The `+` operator on strings is only allowed between compile-time constant strings (literals).",
				DiagnosticIds.DynamicStringConcatenation);
			return;
		}

		if (resolved.ErrorKind == BinaryBindingErrorKind.OperatorOverloadNoMatch && resolved.ErrorOwnerType is { } owner)
		{
			context.Diagnostics.Report(currentFileContext, resolved.Syntax.Span,
				$"No '{resolved.Operator}' operator overload declared on '{owner.Name}' accepts operand type(s) {resolved.LeftType?.Name ?? "?"}, {resolved.RightType?.Name ?? "?"}.",
				DiagnosticIds.OperatorNoMatch);
			return;
		}

		context.Diagnostics.Report(currentFileContext, resolved.Syntax.Span,
			$"Operator '{resolved.Operator}' is not valid/applicable for '{resolved.LeftType?.Name ?? "?"}' and '{resolved.RightType?.Name ?? "?"}'.",
			DiagnosticIds.OperatorNoMatch);
	}

	/// <summary>
	/// Reports a compile-time-known invalid integer edge (division/remainder by zero, signed
	/// division overflow, or out-of-domain shift count) using the shared integer-edge classifier.
	/// </summary>
	private void ReportIntegerEdgeConstantFailure(BinaryExpressionSyntax binary, SymbolTable scope)
	{
		var resolved = GetOrBindBinaryExpression(binary, scope, final: true);
		var failure = IntegerEdgeSemantics.ClassifyConstantEdge(resolved);
		if (failure == IntegerEdgeConstantFailure.None)
			return;

		var (message, diagnosticId) = failure switch
		{
			IntegerEdgeConstantFailure.DivisionByZero => (
				"Division by zero is not allowed: the divisor is a compile-time constant zero.",
				DiagnosticIds.DivisionByZeroConstant),
			IntegerEdgeConstantFailure.SignedMinDivMinusOne => (
				"Signed division/remainder overflow is not allowed: the dividend is the minimum value of its type and the divisor is a compile-time constant -1.",
				DiagnosticIds.SignedMinDivisionOverflow),
			IntegerEdgeConstantFailure.InvalidShiftCount => (
				$"Invalid shift count: the shift count must be within [0, {resolved.IntegerEdgeValueBitWidth}) but is a compile-time constant.",
				DiagnosticIds.InvalidShiftCountConstant),
			_ => ("", ""),
		};

		context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], binary.Span, message, diagnosticId);
	}

	/// <summary>
	/// Returns the semantic result type used by validation for an already parsed expression.
	/// </summary>
	/// <param name="expr">The expression to type.</param>
	/// <param name="scope">The scope the expression is evaluated in.</param>
	/// <param name="expectedType">
	/// The surrounding target type, when the position supplies one. Forwarded only to the
	/// context-typed forms; see <see cref="Check"/>.
	/// </param>
	public TypeSymbol? GetType(ExpressionSyntax expr, SymbolTable scope, TypeSymbol? expectedType = null)
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
			SizeofExpressionSyntax or AlignofExpressionSyntax or OffsetofExpressionSyntax => TypeSymbol.NUInt,
			CallExpressionSyntax call => context.ResolvedCalls.TryGetValue(call, out var resolved) ? resolved.ReturnType
				: context.ResolvedDelegateCalls.TryGetValue(call, out var resolvedDelegate) ? resolvedDelegate.ReturnType
				: null,
			LambdaExpressionSyntax lam => context.ResolvedLambdas.TryGetValue(lam, out var lamInfo) ? lamInfo.Delegate : null,
			MemberAccessExpressionSyntax m => CheckMemberAccessExpression(m, scope, expectedType),
			BorrowExpressionSyntax b => new PointerTypeSymbol(GetType(b.Expression, scope) ?? TypeSymbol.Int, b.IsMutable),
			StructInitializationExpressionSyntax s => GetOrBindStructInitializer(s, scope),
			HeapAllocationExpressionSyntax h => GetType(h.Expression, scope),
			HeapArrayAllocationExpressionSyntax ha => new SliceTypeSymbol(context.ResolveType(ha.ElementTypeName)!),
			IndexExpressionSyntax idx => GetType(idx.Left, scope) switch
			{
				ArrayTypeSymbol array => array.ElementType,
				SliceTypeSymbol slice => slice.ElementType,
				// `T*[i]` is normal indexed storage over the pointee and yields `T`. Raw-pointer
				// indexing deliberately resolves no bounds/null policy here; unchecked C-style
				// pointer arithmetic is the whole point of the form.
				RawPointerTypeSymbol rawPointer => rawPointer.ElementType,
				_ => null,
			},
			ArrayInitializationExpressionSyntax a => CheckArrayInitialization(a, scope),
			ArrayReplicationExpressionSyntax r => CheckArrayReplication(r, scope),
			ParenthesizedStructInitializerExpressionSyntax p => p.ResolvedStructTypeName is not null ? context.ResolveType(p.ResolvedStructTypeName) : null,
			TernaryExpressionSyntax t => CheckTernaryExpression(t, scope),
			CoalesceExpressionSyntax co => GetOrBindCoalesceExpression(co, scope, expectedType).ResultType,
			OptionalChainExpressionSyntax chain => GetOrBindOptionalChain(chain, scope).ResultType,
			VoidLiteralExpressionSyntax => TypeSymbol.Void,
			DefaultExpressionSyntax d => context.ResolveType(d.TypeName),
			UnaryExpressionSyntax unary => GetUnaryExpressionType(unary, scope),
			AsmExpressionSyntax asm => InlineAsm.GetResultType(asm, scope),
			NameofExpressionSyntax => TypeSymbol.String,
			TypeofExpressionSyntax => context.ResolveType("Type"),
			IsPatternExpressionSyntax => TypeSymbol.Bool,
			BinaryExpressionSyntax bin => GetOrBindBinaryExpression(bin, scope).ResultType,
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
						$"Unknown type '{lambdaParam.ExplicitType}' in lambda parameter.", DiagnosticIds.UnknownTypeInLambdaParameter);
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

		// Unsafe usage inside a lambda body belongs to that nested callable, never to the
		// syntactically enclosing function (§ nested callable ownership).
		var prevCallableBody = _validation.CurrentCallableBody;
		_validation.CurrentCallableBody = lam;
		try
		{
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
		finally
		{
			_validation.CurrentCallableBody = prevCallableBody;
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

		var selected = matches[0];

		// A bound-method delegate stores a context pointer to its receiver and may invoke it later,
		// which would require an exclusive mutable borrow-lock for the delegate's whole live range.
		// That contract is not defined yet, so a bound method whose *effective* receiver is mutable
		// (an explicit 'refvar this', or an unmarked extension the body analysis inferred as mutating)
		// is rejected here. The check reads the resolved symbol's receiver mutability rather than the
		// source spelling, so an inferred mutation cannot bypass the restriction.
		if (isBoundMethod
			&& selected.Parameters.Count > 0
			&& selected.Parameters[0].Name == "this"
			&& selected.Parameters[0].Type is PointerTypeSymbol { IsMutable: true })
		{
			context.Diagnostics.Report(currentFileContext, groupRef.Span,
				"Receiver delegation via 'refvar this' is unsupported for bound-method delegates.",
				DiagnosticIds.RefvarThisDelegation);
			return;
		}

		context.ResolvedFunctionConversions[groupRef] = selected;
	}

	/// <summary>
	/// Returns true when the operand of unary '&amp;' denotes a visible function group rather than
	/// ordinary value storage. This keeps raw data-address semantics separate from target-typed
	/// native function addresses without introducing a second syntax node.
	/// </summary>
	private bool IsFunctionAddressOperand(ExpressionSyntax operand, SymbolTable scope)
	{
		if (operand is IdentifierExpressionSyntax id)
		{
			if (Calls.IsKnownVariable(id, scope))
				return false;
			if (Overloads.HasCandidates(id.Name))
				return true;
			return context.GenericFunctionTemplates.ContainsKey(context.GetMangledName(id.Name, context.CurrentNamespace))
				|| context.GenericFunctionTemplates.ContainsKey(id.Name);
		}

		if (operand is MemberAccessExpressionSyntax member)
		{
			var name = GetQualifiedMemberPath(member);
			if (name is not null)
			{
				var candidates = new List<FunctionSymbol>();
				Overloads.GatherCandidates(name, candidates);
				if (candidates.Count > 0)
					return true;
			}

			// A bound extension method is a callable group, but it cannot become a raw native
			// function pointer because doing so would lose its receiver/context. Recognize it here
			// so the dedicated function-address diagnostic is emitted instead of a data-address path.
			return IsMethodGroupReference(member, scope);
		}

		return false;
	}

	/// <summary>
	/// Contextually resolves one <c>&amp;Function</c> expression against an expected nominal native
	/// delegate and records the exact function selected for code generation.
	/// </summary>
	private void BindNativeFunctionAddress(
		UnaryExpressionSyntax address,
		DelegateTypeSymbol delegateType,
		SymbolTable scope)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		_validation.RequireUnsafe(context, address, UnsafeUsageKind.NativeFunctionAddress,
			"Taking a native function address requires an unsafe context.",
			DiagnosticIds.FunctionAddressRequiresUnsafe);

		var candidates = new List<FunctionSymbol>();
		var isBoundMethod = false;
		switch (address.Operand)
		{
			case IdentifierExpressionSyntax id:
				{
					var mangledTemplate = context.GetMangledName(id.Name, context.CurrentNamespace);
					if (context.GenericFunctionTemplates.ContainsKey(mangledTemplate)
						|| context.GenericFunctionTemplates.ContainsKey(id.Name))
					{
						context.Diagnostics.Report(currentFileContext, address.Span,
							$"Taking the address of generic function '{id.Name}' is not supported in this increment.",
							DiagnosticIds.FunctionAddressGeneric);
						return;
					}

					Overloads.GatherCandidates(id.Name, candidates);
					break;
				}
			case MemberAccessExpressionSyntax member:
				{
					var qualifiedName = GetQualifiedMemberPath(member);
					if (qualifiedName is not null)
						Overloads.GatherCandidates(qualifiedName, candidates);

					if (candidates.Count == 0 && IsMethodGroupReference(member, scope))
					{
						isBoundMethod = true;
						var receiverType = GetType(member.Expression, scope);
						if (receiverType is PointerTypeSymbol ptr)
							receiverType = ptr.ReferencedType;
						if (receiverType is not null)
							candidates.AddRange(context.GetExtensionMethodCandidates(receiverType, context.CurrentUnit, member.MemberName)
								.Select(candidate => candidate.Function));
					}

					break;
				}
		}

		if (isBoundMethod)
		{
			context.Diagnostics.Report(currentFileContext, address.Span,
				"A bound method cannot be converted to a native function pointer because it requires receiver state.",
				DiagnosticIds.FunctionAddressNotAddressable);
			return;
		}

		var addressable = candidates.Where(IsNativeAddressableFunction).ToList();
		if (addressable.Count == 0)
		{
			context.Diagnostics.Report(currentFileContext, address.Span,
				"Function is not addressable as a native callback. Use an imported extern, expose extern, or unsafe native-ABI Cvolo function.",
				DiagnosticIds.FunctionAddressNotAddressable);
			return;
		}

		var signatureMatches = addressable.Where(function => NativeFunctionSignatureMatches(function, delegateType)).ToList();
		if (signatureMatches.Count == 0)
		{
			context.Diagnostics.Report(currentFileContext, address.Span,
				$"No addressable function matches native delegate '{delegateType.Name}' parameter and return types.",
				DiagnosticIds.FunctionAddressSignatureMismatch);
			return;
		}

		var conventionMatches = signatureMatches
			.Where(function => string.Equals(GetSourceCallingConvention(function), delegateType.CallingConvention, StringComparison.Ordinal))
			.ToList();
		if (conventionMatches.Count == 0)
		{
			context.Diagnostics.Report(currentFileContext, address.Span,
				$"Function calling convention does not match native delegate '{delegateType.Name}' ({delegateType.CallingConvention}).",
				DiagnosticIds.FunctionAddressCallingConventionMismatch);
			return;
		}

		if (conventionMatches.Count > 1)
		{
			context.Diagnostics.Report(currentFileContext, address.Span,
				"Function address is ambiguous for the expected native delegate signature.",
				DiagnosticIds.FunctionAddressOverloadAmbiguous);
			return;
		}

		context.ResolvedNativeFunctionAddresses[address] = (conventionMatches[0], delegateType);
	}

	private static bool NativeFunctionSignatureMatches(FunctionSymbol function, DelegateTypeSymbol delegateType)
	{
		if (function.IsVariadic || function.Parameters.Count != delegateType.Parameters.Count)
			return false;

		if (!function.ReturnType.Equals(delegateType.ReturnType))
			return false;

		for (var i = 0; i < function.Parameters.Count; i++)
		{
			if (!function.Parameters[i].Type.Equals(delegateType.Parameters[i].Type))
				return false;
		}

		return true;
	}

	private static bool IsNativeAddressableFunction(FunctionSymbol function) =>
		function.IsExtern || function.IsExported || function.IsNativeAbi;

	private static string GetSourceCallingConvention(FunctionSymbol function) =>
		function.CallingConvention ?? "C";

	private static string? GetQualifiedMemberPath(ExpressionSyntax expression) => expression switch
	{
		IdentifierExpressionSyntax id => id.Name,
		MemberAccessExpressionSyntax member when GetQualifiedMemberPath(member.Expression) is { } prefix => $"{prefix}.{member.MemberName}",
		_ => null,
	};

	/// <summary>
	/// Checks an expression that supplies a delegate-typed value: target-typed lambdas,
	/// function/method group conversions, rejected null literals, or a plain value expression.
	/// Shared by variable declarations, struct/union member initializers, and parameter passes.
	/// </summary>
	public void CheckDelegateValue(ExpressionSyntax expr, DelegateTypeSymbol delegateType, SymbolTable scope)
	{
		if (expr is UnaryExpressionSyntax { Operator: "&" } functionAddress
			&& IsFunctionAddressOperand(functionAddress.Operand, scope))
		{
			if (!delegateType.IsNative)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span,
					$"Function addresses can only be converted to native delegates; '{delegateType.Name}' is a safe delegate.",
					DiagnosticIds.SafeNativeDelegateConversion);
				return;
			}

			BindNativeFunctionAddress(functionAddress, delegateType, scope);
			return;
		}

		if (expr is LambdaExpressionSyntax targetLambda)
		{
			if (delegateType.IsNative)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span,
					$"Native delegate '{delegateType.Name}' cannot be created from a safe lambda; use the address of a compatible native-ABI function.",
					DiagnosticIds.InvalidFunctionConversion);
			}
			else
			{
				CheckTargetTypedLambda(targetLambda, delegateType, scope);
			}
		}
		else if (expr is NullLiteralExpressionSyntax)
		{
			if (!delegateType.IsNative)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, expr.Span,
					$"Cannot initialize delegate '{delegateType.Name}' with 'null'; delegates are non-null values.",
					DiagnosticIds.NullLiteralForDelegate);
			}
		}
		else if (expr is IdentifierExpressionSyntax groupId && !Calls.IsKnownVariable(groupId, scope) && Overloads.HasCandidates(groupId.Name))
		{
			if (delegateType.IsNative)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, groupId.Span,
					$"Native delegate '{delegateType.Name}' requires an explicit function address; use '&{groupId.Name}'.",
					DiagnosticIds.InvalidFunctionConversion);
			}
			else
			{
				CheckFunctionGroupConversion(groupId, delegateType, scope);
			}
		}
		else if (expr is MemberAccessExpressionSyntax groupMa && IsMethodGroupReference(groupMa, scope))
		{
			if (delegateType.IsNative)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, groupMa.Span,
					$"Native delegate '{delegateType.Name}' requires an explicit function address ('&Function'); implicit function-group conversion is not defined.",
					DiagnosticIds.InvalidFunctionConversion);
			}
			else
			{
				CheckFunctionGroupConversion(groupMa, delegateType, scope);
			}
		}
		else
		{
			Check(expr, scope);
			var actualType = GetType(expr, scope);
			if (actualType is DelegateTypeSymbol actualDelegate)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				if (actualDelegate.IsNative != delegateType.IsNative)
				{
					context.Diagnostics.Report(currentFileContext, expr.Span,
						"Conversion between safe delegates and native delegates is not defined.",
						DiagnosticIds.SafeNativeDelegateConversion);
				}
				else if (delegateType.IsNative && !actualDelegate.Equals(delegateType))
				{
					context.Diagnostics.Report(currentFileContext, expr.Span,
						$"Implicit conversion between distinct native delegate types '{actualDelegate.Name}' and '{delegateType.Name}' is not allowed.",
						DiagnosticIds.InvalidFunctionConversion);
				}
			}
		}
	}

	/// <summary>True if the member access names a zero-arg-this extension member on the
	/// receiver's type (a bound-method group) rather than a struct/union field.</summary>
	public bool IsMethodGroupReference(MemberAccessExpressionSyntax ma, SymbolTable scope)
	{
		// Namespace-qualified globals are values, not method groups. Resolve the full
		// access before inspecting its receiver so namespace prefixes are not checked
		// as ordinary variables (for example: System.Math.Int.MaxValue).
		if (TryResolveNamespaceGlobal(ma, scope, out _))
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
		if (expr.ResolvedStructTypeName is null)
			return;
		var type = context.ResolveType(expr.ResolvedStructTypeName);
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
			else if (field.Type is DelegateTypeSymbol delegateFieldType)
			{
				CheckDelegateValue(init.Expression, delegateFieldType, scope);
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
				$"Union '{unionType.Name}' is {size} bytes. Passing by value is forbidden for unions larger than 16 bytes; pass by 'ref'/'refvar' instead.", DiagnosticIds.UnionIsBytesPassingByValueIsForbiddenForUnionsLargerThan16By);
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
			$"Implicit conversion between enum '{enumType.Name}' and '{otherType.Name}' is forbidden; use an explicit cast.", DiagnosticIds.ImplicitConversionBetweenEnumAndIsForbiddenUseAnExplicitCast);
	}

	/// <summary>
	/// Validates an explicit unary cast and reports unsupported or unsafe conversions.
	/// </summary>
	private void CheckUnaryCast(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		if (!unary.Operator.StartsWith('(') || !unary.Operator.EndsWith(')') || unary.Operator.Length < 3)
			return;

		var targetTypeName = unary.Operator[1..^1];
		var targetType = context.ResolveType(targetTypeName);
		var operandType = GetType(unary.Operand, scope);
		if (targetType is null || operandType is null)
			return;

		var targetDelegate = targetType as DelegateTypeSymbol;
		var operandDelegate = operandType as DelegateTypeSymbol;
		var targetRawPointer = targetType as RawPointerTypeSymbol;
		var operandRawPointer = operandType as RawPointerTypeSymbol;

		// Safe/native delegates have deliberately incompatible representations. No explicit
		// reinterpret bridge is provided between {invoke,context} and one-word native addresses.
		if ((targetDelegate is { IsNative: true } && operandDelegate is { IsNative: false })
			|| (targetDelegate is { IsNative: false } && operandDelegate is { IsNative: true }))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, unary.Span,
				"Explicit conversion between safe delegates and native delegates is not defined.",
				DiagnosticIds.SafeNativeDelegateReinterpret);
			return;
		}

		// Native-delegate reinterpretation is intentionally narrow: raw pointer <-> native
		// delegate and native delegate <-> native delegate, all only in an unsafe context.
		var isNativeReinterpret =
			(targetDelegate is { IsNative: true } && (operandRawPointer is not null || operandDelegate is { IsNative: true }))
			|| (operandDelegate is { IsNative: true } && targetRawPointer is not null);
		if (isNativeReinterpret)
		{
			_validation.RequireUnsafe(context, unary, UnsafeUsageKind.NativeDelegateCast,
				"Casting a native delegate to/from a raw pointer or another native delegate requires an unsafe context.",
				DiagnosticIds.NativeDelegateCastRequiresUnsafe);
			return;
		}

		// Any other cast whose target is a raw pointer is a raw-pointer cast. It may be a valid
		// reinterpretation but still only belongs in unsafe code; record the canonical usage so
		// UnsafeBody effectiveness sees it. The operation-specific safety check lives with the
		// raw-pointer rules below and in the safety validator.
		if (targetRawPointer is not null)
			_validation.RecordUnsafeUsage(UnsafeUsageKind.RawPointerCast);

		// From here down preserve the existing destructive raw-pointer cast rules.
		if (targetRawPointer is null)
			return;

		if (operandType is UnionTypeSymbol optionUnion && optionUnion.IsNpoEligible)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, unary.Span,
				$"Cannot cast nullable reference option '{optionUnion.Name}' directly to a raw pointer; pattern-match it (switch on 'ref'/'refvar') to extract a non-null reference first.", DiagnosticIds.CannotCastNullableReferenceOptionDirectlyToARawPointerPatter);
			return;
		}

		// Destructive cast '(T*)x' extracts the owning heap pointer from a heap-allocated
		// handle. A plain stack value has no hidden pointer to extract, so reject it here.
		if (targetRawPointer.ElementType.Equals(operandType) && unary.Operand is IdentifierExpressionSyntax id)
		{
			var sym = scope.Lookup(id.Name) as VariableSymbol;
			if (sym is not null && !sym.IsHeapAllocated && sym.Origin != OriginKind.Parameter)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, unary.Span,
					$"Destructive cast '({targetTypeName})' requires an owning heap handle; '{id.Name}' is a stack value. Allocate it with 'heap {operandType.Name} {{ ... }}' or 'heap {operandType.Name}(...)', or cast its address with '&{id.Name}'.", DiagnosticIds.DestructiveCastRequiresAnOwningHeapHandleIsAStackValueAlloca);
			}
		}

		if (targetRawPointer.ElementType.Equals(TypeSymbol.Char)
			&& operandType.Equals(TypeSymbol.String))
		{
			_validation.RequireUnsafe(context, unary, UnsafeUsageKind.RawPointerCast,
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
			context.Diagnostics.Report(currentFileContext, isPat.Span, $"The 'is' pattern can only be applied to a union type, got '{operandType.Name}'.", DiagnosticIds.TheIsPatternCanOnlyBeAppliedToAUnionTypeGot);
			return;
		}

		if (unionType.IsUnsafe)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				isPat.Span,
				$"Cannot apply the 'is' pattern to raw 'unsafe union' '{unionType.Name}'; it has no tag and cannot be pattern-matched over variants.",
				DiagnosticIds.UnsafeUnionTaggedOperation);
			return;
		}

		var variant = unionType.FindField(isPat.VariantName);
		if (variant is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, isPat.Span, $"Union '{unionType.Name}' does not contain variant '{isPat.VariantName}'", DiagnosticIds.UnionDoesNotContainVariant);
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
				context.Diagnostics.Report(currentFileContext, isPat.Span, $"Void variant '{isPat.VariantName}' cannot carry a bound variable.", DiagnosticIds.VoidVariantCannotCarryABoundVariable);
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
			$"Operator '~' cannot be applied to non-[Flags] enum '{enumType.Name}'.", DiagnosticIds.OperatorCannotBeAppliedToNonFlagsEnum);
	}

	/// <summary>
	/// Returns the semantic result type produced by a unary expression.
	/// </summary>
	private TypeSymbol? GetUnaryExpressionType(UnaryExpressionSyntax unary, SymbolTable scope)
	{
		// Operator overloads (§14) answer before the builtin meanings of '-', '!' and '~'.
		if (GetType(unary.Operand, scope) is { } unaryOperand
			&& context.FindOperatorOverload(unary.Operator, unaryOperand) is { } overloaded)
			return overloaded.ReturnType;

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
	/// Reports an operator expression whose operand type declares the operator but accepts none of
	/// these operands. Without it the expression falls back to builtin lowering and fails later with
	/// an unrelated type error, or silently ignores the declared operator.
	/// </summary>
	private void ReportMissingOperatorOverload(string spelling, TypeSymbol? left, TypeSymbol? right, bool unary, TextSpan span)
	{
		if (OperatorTokens.TryGetToken(spelling, unary) is null)
			return;

		var owner = left is not null && context.DeclaresOperatorOverload(spelling, left, unary)
			? left
			: right is not null && context.DeclaresOperatorOverload(spelling, right, unary)
				? right
				: null;
		if (owner is null)
			return;

		var operandTypes = unary ? left?.Name ?? "?" : $"{left?.Name ?? "?"}, {right?.Name ?? "?"}";
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, span,
			$"No '{spelling}' operator overload declared on '{owner.Name}' accepts operand type(s) {operandTypes}.",
			DiagnosticIds.OperatorNoMatch);
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
