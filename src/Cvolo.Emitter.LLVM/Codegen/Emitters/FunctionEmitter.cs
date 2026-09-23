using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Emitter.LLVM.Codegen.TypeLowering;
using Cvolo.Emitter.LLVM.Codegen.Values;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits the body of one already-declared LLVM function and establishes the function-local
/// code-generation state used by statement, expression, call, and cleanup emitters.
/// </summary>
/// <remarks>
/// Declaration/linkage decisions remain module-level concerns in <see cref="CodeGenerator"/>.
/// This emitter owns only definition-time work: entry block creation, function-frame setup,
/// parameter binding, checked-FFI prologues, delegating-constructor calls, body emission, and the
/// implicit void return path. Native ABI classification is intentionally not introduced here;
/// existing FFI lowering is supplied through a migration callback so behavior stays unchanged.
/// </remarks>
/// <remarks>
/// Creates a function-body emitter over the shared module state and the existing specialized
/// emitters used while a function definition is being generated.
/// </remarks>
internal sealed class FunctionEmitter(
	CodegenContext codegen,
	CleanupEmitter cleanup,
	Func<FunctionCodegenContext> getFunction,
	Action<FunctionCodegenContext> setFunction,
	Func<TypeSymbol, LLVMTypeRef> lowerFfiType,
	ValueLoader values,
	Func<CallExpressionSyntax, LLVMValueRef?, LLVMValueRef> emitCall,
	Action<BlockStatementSyntax> emitBlock,
	Action emitTrap,
	IReadOnlyDictionary<string, ConstructorDeclarationSyntax> constructorInitializers,
	bool checkedFfiBounds)
{
	private LLVMBuilderRef Builder => codegen.Builder;
	private FunctionCodegenContext Function => getFunction();
	private BindingContext BindingContext => codegen.BindingContext
		?? throw new InvalidOperationException("Function emission requires an active binding context.");
	private readonly NativeAbiAggregateLowering _nativeAggregates = new(codegen, lowerFfiType);

	/// <summary>
	/// Emits one function definition using the LLVM declaration previously registered under
	/// <paramref name="mangledName"/>. Bodyless declarations are intentionally ignored.
	/// </summary>
	public void EmitBody(FunctionDeclarationSyntax function, string mangledName)
	{
		if (!function.HasBody)
			return;

		if (!codegen.Globals.TryGetValue(mangledName, out var llvmFunction))
			return;

		var entry = llvmFunction.AppendBasicBlock("entry");
		Builder.PositionAtEnd(entry);

		setFunction(CreateFunctionContext(mangledName));
		SeedVisibleGlobals();

		var functionSymbol = BindingContext.Globals.Lookup(mangledName) as FunctionSymbol;
		if (functionSymbol is not null)
		{
			if (codegen.NativeAbiFunctionPlans.TryGetValue(mangledName, out var nativePlan))
			{
				Function.NativeAbiPlan = nativePlan;
				if (nativePlan.HasSRet)
					Function.NativeSRetPointer = llvmFunction.GetParam(0);
			}

			if (functionSymbol.IsExported && checkedFfiBounds && functionSymbol.Parameters.Count > 0)
				EmitCheckedFfiBoundsGuard(llvmFunction, functionSymbol);

			BindResolvedParameters(llvmFunction, functionSymbol);
		}
		else
		{
			BindFallbackParameters(llvmFunction, function);
		}

		EmitDelegatingConstructorCall(mangledName);
		emitBlock(function.Body);

		if (function.ReturnType == "void" && !StatementEmitter.EndsWithReturn(function.Body))
		{
			cleanup.EmitScopeCleanup(
				Function,
				[.. Function.Locals.Keys],
				skipHeapFree: Function.OwnershipTransferFunction);
			Builder.BuildRetVoid();
		}

		Function.UnsafeDepth = 0;
	}

	/// <summary>
	/// Creates fresh function-local state and initializes safety/ownership flags from the resolved
	/// semantic function symbol and return type.
	/// </summary>
	private FunctionCodegenContext CreateFunctionContext(string mangledName)
	{
		var context = new FunctionCodegenContext();
		var functionSymbol = BindingContext.Globals.Lookup(mangledName) as FunctionSymbol
			?? (BindingContext.MonomorphizedFunctions.TryGetValue(mangledName, out var monomorphized)
				? monomorphized
				: null);

		context.UnsafeDepth = functionSymbol is not null
			&& (functionSymbol.SafetyTier == SafetyTier.Unsafe || functionSymbol.IsUnsafeBody)
			? 1
			: 0;

		context.OwnershipTransferFunction = functionSymbol is not null
			&& functionSymbol.SafetyTier == SafetyTier.Unbound
			&& codegen.FunctionReturnTypes.TryGetValue(mangledName, out var returnType)
			&& cleanup.TypeEscapesHeap(returnType);

		return context;
	}

	/// <summary>
	/// Seeds module globals and unambiguous short-name aliases into the fresh function frame so
	/// normal local load/store/addressing machinery can resolve them without special cases.
	/// </summary>
	private void SeedVisibleGlobals()
	{
		foreach (var (globalName, globalRef) in codegen.GlobalVariables)
		{
			if (!Function.Locals.ContainsKey(globalName))
				Function.Locals[globalName] = globalRef;
		}

		foreach (var (globalName, globalType) in codegen.GlobalVariableTypes)
		{
			if (!Function.VariableTypes.ContainsKey(globalName))
				Function.VariableTypes[globalName] = globalType;
		}

		foreach (var (shortName, _) in codegen.GlobalShortNames)
		{
			if (Function.Locals.ContainsKey(shortName))
				continue;

			var resolvedKey = values.ResolveGlobalKey(shortName);
			if (resolvedKey is null)
				continue;

			Function.Locals[shortName] = codegen.GlobalVariables[resolvedKey];
			Function.VariableTypes[shortName] = codegen.GlobalVariableTypes[resolvedKey];
		}
	}

	/// <summary>
	/// Emits the existing checked-FFI null-pointer prologue for exported pointer parameters and
	/// branches to the shared trap path when any checked argument is null.
	/// </summary>
	private void EmitCheckedFfiBoundsGuard(LLVMValueRef llvmFunction, FunctionSymbol functionSymbol)
	{
		var bodyBlock = llvmFunction.AppendBasicBlock("ffi.body");
		LLVMValueRef? anyNull = null;

		for (var i = 0; i < functionSymbol.Parameters.Count; i++)
		{
			if (functionSymbol.Parameters[i].Type is not (PointerTypeSymbol or RawPointerTypeSymbol))
				continue;

			var parameterOffset = Function.NativeAbiPlan?.HasSRet == true ? 1u : 0u;
			var parameter = llvmFunction.GetParam((uint)i + parameterOffset);
			var isNull = Builder.BuildICmp(
				LLVMIntPredicate.LLVMIntEQ,
				parameter,
				LLVMValueRef.CreateConstPointerNull(parameter.TypeOf),
				$"null.{functionSymbol.Parameters[i].Name}");
			anyNull = anyNull is null ? isNull : Builder.BuildOr(anyNull.Value, isNull, "any_null");
		}

		if (anyNull is not null)
		{
			var trapBlock = llvmFunction.AppendBasicBlock("ffi.trap");
			Builder.BuildCondBr(anyNull.Value, trapBlock, bodyBlock);
			Builder.PositionAtEnd(trapBlock);
			emitTrap();
		}
		else
		{
			Builder.BuildBr(bodyBlock);
		}

		Builder.PositionAtEnd(bodyBlock);
	}

	/// <summary>
	/// Binds parameters from the resolved semantic function symbol into addressable local storage,
	/// preserving the current exported-bool ABI conversion behavior.
	/// </summary>
	private void BindResolvedParameters(LLVMValueRef llvmFunction, FunctionSymbol functionSymbol)
	{
		var isNativeBoundary = functionSymbol.IsExported || functionSymbol.IsNativeAbi;
		var nativePlan = Function.NativeAbiPlan;
		var llvmParameterIndex = nativePlan?.HasSRet == true ? 1 : 0;

		for (var i = 0; i < functionSymbol.Parameters.Count; i++, llvmParameterIndex++)
		{
			var parameter = llvmFunction.GetParam((uint)llvmParameterIndex);
			var parameterName = functionSymbol.Parameters[i].Name;
			parameter.Name = parameterName;
			var typeSymbol = functionSymbol.Parameters[i].Type;
			var valuePlan = nativePlan is not null ? nativePlan.Parameters[i] : null;

			if (valuePlan is { Kind: NativeAbiValuePassKind.IndirectAggregate })
			{
				Function.Locals[parameterName] = parameter;
				Function.VariableTypes[parameterName] = typeSymbol;
				continue;
			}

			if (valuePlan is { Kind: NativeAbiValuePassKind.DirectIntegerAggregate })
			{
				var aggregateStorage = _nativeAggregates.MaterializeDirectAggregate(
					Builder, valuePlan, parameter, parameterName + ".from_abi");
				Function.Locals[parameterName] = aggregateStorage;
				Function.VariableTypes[parameterName] = typeSymbol;
				continue;
			}

			// Locals always use the internal Cvolo representation. Native-boundary scalar
			// parameters are converted once at entry before being stored.
			var llvmType = codegen.Types.Lower(typeSymbol);
			var alloca = Builder.BuildAlloca(llvmType, parameterName);

			if (isNativeBoundary && typeSymbol.Name == "bool"
				&& parameter.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind
				&& parameter.TypeOf.IntWidth > 1)
			{
				parameter = Builder.BuildTrunc(parameter, LLVMTypeRef.Int1, "bool.from_abi");
			}

			Builder.BuildStore(parameter, alloca);
			Function.Locals[parameterName] = alloca;
			Function.VariableTypes[parameterName] = typeSymbol;
		}
	}

	/// <summary>
	/// Binds parameters directly from syntax when no resolved function symbol is available, matching
	/// the legacy fallback used by code generation before this extraction.
	/// </summary>
	private void BindFallbackParameters(LLVMValueRef llvmFunction, FunctionDeclarationSyntax function)
	{
		for (var i = 0; i < function.Parameters.Count; i++)
		{
			var parameter = llvmFunction.GetParam((uint)i);
			var parameterName = function.Parameters[i].Name;
			parameter.Name = parameterName;

			var typeSymbol = BindingContext.ResolveType(function.Parameters[i].Type)!;
			var llvmType = codegen.Types.Lower(typeSymbol);
			var ffiType = function.CallingConvention is not null ? lowerFfiType(typeSymbol) : llvmType;
			var alloca = Builder.BuildAlloca(llvmType, parameterName);

			if (function.CallingConvention is not null
				&& typeSymbol.Equals(TypeSymbol.Bool)
				&& parameter.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind
				&& ffiType.Kind == LLVMTypeKind.LLVMIntegerTypeKind
				&& parameter.TypeOf.IntWidth > llvmType.IntWidth)
			{
				// Fallback declarations still keep locals in Cvolo's internal i1 representation.
				parameter = Builder.BuildTrunc(parameter, LLVMTypeRef.Int1, "bool.from_abi");
			}

			Builder.BuildStore(parameter, alloca);

			Function.Locals[parameterName] = alloca;
			Function.VariableTypes[parameterName] = typeSymbol;
		}
	}

	/// <summary>
	/// Emits a delegating constructor initializer against the current <c>this</c> destination before
	/// the constructor body executes.
	/// </summary>
	private void EmitDelegatingConstructorCall(string mangledName)
	{
		if (!constructorInitializers.TryGetValue(mangledName, out var chainedConstructor)
			|| !Function.Locals.TryGetValue("this", out var thisStorage)
			|| !BindingContext.ConstructorDelegationTargets.TryGetValue(mangledName, out var chainTarget))
		{
			return;
		}

		var thisPointer = Builder.BuildLoad2(
			LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
			thisStorage,
			"chained_this");

		var chainCall = new CallExpressionSyntax(
			chainedConstructor.ConstructorInitializerSpan ?? chainedConstructor.Span,
			chainedConstructor.StructName,
			[],
			chainedConstructor.ConstructorArguments!);

		BindingContext.ResolvedCalls[chainCall] = chainTarget;
		emitCall(chainCall, thisPointer);
	}
}
