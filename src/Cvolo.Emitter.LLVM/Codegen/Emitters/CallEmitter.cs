using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Emitter.LLVM.Codegen.Values;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits LLVM call sites for direct functions, safe delegate values, compiler-synthesized calls,
/// and LLVM intrinsics.
/// </summary>
/// <remarks>
/// This extraction intentionally preserves the existing call semantics. Binding, overload
/// resolution, delegate value construction, native ABI classification, and general expression
/// emission remain outside this class. Aggregate/member/index addressing is delegated to
/// <see cref="AggregateEmitter"/>, while semantic expression typing is shared through
/// <see cref="ExpressionTypeResolver"/>.
/// </remarks>
/// <remarks>
/// Creates a call emitter backed by module-level codegen state and the current function frame.
/// </remarks>
internal sealed class CallEmitter(
	CodegenContext codegen,
	CleanupEmitter cleanup,
	AggregateEmitter aggregates,
	Func<FunctionCodegenContext> getFunction,
	Func<ExpressionSyntax, LLVMValueRef> emitExpression,
	ExpressionTypeResolver expressionTypes,
	ValueCoercion coercion,
	Func<string, LLVMValueRef> load,
	IReadOnlyDictionary<string, ExternDeclarationSyntax> astExterns,
	IReadOnlyDictionary<string, ExternBlockFunctionSyntax> astExternBlockFunctions)
{
	private LLVMBuilderRef Builder => codegen.Builder;
	private FunctionCodegenContext Function => getFunction();
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Call emission requires an active binding context.");

	/// <summary>
	/// Emits a call expression using the currently active function frame.
	/// </summary>
	/// <param name="call">Bound call expression to lower.</param>
	/// <param name="implicitThisPtr">Optional destination/receiver pointer used by constructor calls.</param>
	public LLVMValueRef Emit(CallExpressionSyntax call, LLVMValueRef? implicitThisPtr = null)
	{
		var paramOffset = implicitThisPtr is not null ? 1 : 0;
		return EmitCore(call, implicitThisPtr, paramOffset);
	}

	private LLVMValueRef EmitCore(CallExpressionSyntax call, LLVMValueRef? implicitThisPtr, int paramOffset)
	{
		if (call.FunctionName == "sizeof")
		{
			var targetTypeName = call.TypeArguments[0];
			var targetType = BindingContext.ResolveType(targetTypeName)!;
			var size = codegen.AggregateLayout.GetByteSize(targetType);
			return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)size);
		}

		// Safe delegates are represented as { invoke thunk, context }. Invocation is uniform
		// regardless of whether the value originated from a free function, bound method, or closure.
		if (BindingContext.ResolvedDelegateCalls.TryGetValue(call, out var delegateType))
			return EmitDelegateInvocation(call, delegateType);

		if (BindingContext.ResolvedCalls.TryGetValue(call, out var intrinsicFunc)
			&& !string.IsNullOrEmpty(intrinsicFunc.IntrinsicName))
		{
			return EmitIntrinsicCall(call, intrinsicFunc);
		}

		// Synthesized enum Name() lowers to a select chain over compile-time string constants.
		if (BindingContext.ResolvedCalls.TryGetValue(call, out var nameFunc)
			&& nameFunc.Name.StartsWith("$Name$", StringComparison.Ordinal))
		{
			var receiverName = call.FunctionName[..call.FunctionName.IndexOf('.')];
			var enumType = GetBoundEnumReceiverType(nameFunc) ?? throw new InvalidOperationException($"Name() requires an enum receiver but found '{receiverName}'.");
			var receiverValue = load(receiverName);
			var storageTy = codegen.Types.Lower(enumType);
			var nameResult = Builder.BuildGlobalStringPtr("(unknown)", $"enum_name_{enumType.Name}_unknown");
			foreach (var variant in enumType.Variants)
			{
				var isMatch = Builder.BuildICmp(
					LLVMIntPredicate.LLVMIntEQ,
					receiverValue,
					LLVMValueRef.CreateConstInt(storageTy, unchecked((ulong)variant.Value)),
					"enum_name_cmp");
				nameResult = Builder.BuildSelect(
					isMatch,
					Builder.BuildGlobalStringPtr(variant.Name, $"enum_name_{enumType.Name}_{variant.Name}"),
					nameResult,
					"enum_name_result");
			}

			return nameResult;
		}

		// Synthesized [Flags] HasFlag lowers inline to (value & flag) == flag.
		if (BindingContext.ResolvedCalls.TryGetValue(call, out var hasFlagFunc)
			&& hasFlagFunc.Name.StartsWith("$HasFlag$", StringComparison.Ordinal))
		{
			var receiverName = call.FunctionName[..call.FunctionName.IndexOf('.')];
			LLVMValueRef receiverValue;
			if (Function.Locals.ContainsKey(receiverName))
				receiverValue = load(receiverName);
			else
				receiverValue = emitExpression(call.Arguments[0]);

			var flagValue = emitExpression(call.Arguments[0]);
			var andVal = Builder.BuildAnd(receiverValue, flagValue, "hasflag_and");
			return Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, andVal, flagValue, "hasflag_result");
		}

		string emitName;
		if (BindingContext.ResolvedCalls.TryGetValue(call, out var resolvedFunc))
		{
			emitName = resolvedFunc.Name;
		}
		else
		{
			var currentUnit = codegen.CurrentUnit
				?? throw new InvalidOperationException("Call emission requires an active compilation unit.");
			emitName = ResolveFunctionName(call.FunctionName, currentUnit);
			if (call.TypeArguments.Count > 0)
				emitName = $"{emitName}<{string.Join(", ", call.TypeArguments)}>";
		}

		var callee = codegen.Globals[emitName];
		var funcType = codegen.FunctionTypes[emitName];
		var args = new List<LLVMValueRef>();

		var isExtensionCall = BindingContext.ResolvedCalls.TryGetValue(call, out var resolvedExt)
			&& resolvedExt.Parameters.Count > 0
			&& resolvedExt.Parameters[0].Name == "this";

		if (isExtensionCall && call.FunctionName.Contains('.'))
		{
			var lastDot = call.FunctionName.LastIndexOf('.');
			var receiverName = call.FunctionName[..lastDot];

			LLVMValueRef receiverPtr = default;
			TypeSymbol receiverType = null!;
			var found = false;

			if (Function.Locals.TryGetValue(receiverName, out var localPtr))
			{
				receiverPtr = localPtr;
				receiverType = Function.VariableTypes[receiverName];
				found = true;
			}
			else if ((codegen.GlobalVariables.ContainsKey(receiverName) ? receiverName : ResolveGlobalKey(receiverName)) is { } recvKey
				&& codegen.GlobalVariables.TryGetValue(recvKey, out var globalPtr))
			{
				receiverPtr = globalPtr;
				receiverType = codegen.GlobalVariableTypes[recvKey];
				found = true;
			}
			else if (Function.Locals.TryGetValue("this", out var thisPtr))
			{
				var thisType = Function.VariableTypes["this"] as PointerTypeSymbol;
				var structType = thisType?.ReferencedType as StructTypeSymbol;
				var field = structType?.FindField(receiverName);
				if (field is not null)
				{
					var actualThisPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisPtr, "loaded_this_ptr");
					var fieldIndex = codegen.AggregateLayout.GetFieldIndex(structType!, receiverName);
					var structLayoutTy = codegen.Types.Lower(structType!);
					var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
					var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);

					receiverPtr = Builder.BuildGEP2(structLayoutTy, actualThisPtr, new LLVMValueRef[] { zero, index }, "this_field_ptr");
					receiverType = field.Type;
					found = true;
				}
			}

			if (!found)
				throw new InvalidOperationException($"Cannot resolve receiver '{receiverName}' for method call '{call.FunctionName}'.");

			if (receiverType is PointerTypeSymbol)
				args.Add(Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), receiverPtr, "receiver_loaded_ptr"));
			else
				args.Add(receiverPtr);
		}
		else if (implicitThisPtr is not null)
		{
			args.Add(implicitThisPtr.Value);
		}
		else if (isExtensionCall)
		{
			if (!Function.Locals.TryGetValue("this", out var thisSlot))
				throw new InvalidOperationException($"Cannot resolve implicit receiver 'this' for method call '{call.FunctionName}'.");

			args.Add(Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisSlot, "loaded_this_ptr"));
		}

		var actualParamOffset = paramOffset + (isExtensionCall ? 1 : 0);

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var argExpr = call.Arguments[i];
			LLVMValueRef val;
			var valTy = expressionTypes.Resolve(argExpr);
			var paramTy = GetParamType(emitName, i + actualParamOffset);

			var targetSlice = paramTy is SliceTypeSymbol sl
				? sl
				: (paramTy is PointerTypeSymbol pPtr && pPtr.ReferencedType is SliceTypeSymbol sRef ? sRef : null);

			var isArgArray = valTy is ArrayTypeSymbol
				|| (valTy is PointerTypeSymbol aPtr && aPtr.ReferencedType is ArrayTypeSymbol);

			if (targetSlice is not null && isArgArray)
			{
				LLVMValueRef arrayPtr;
				if (argExpr is IdentifierExpressionSyntax id)
					arrayPtr = Function.Locals[id.Name];
				else
					(arrayPtr, _, _, _) = aggregates.GetFieldPointer(argExpr);

				val = coercion.CoerceArrayToSlice(arrayPtr, valTy, targetSlice);
			}
			else
			{
				val = emitExpression(argExpr);
			}

			if (paramTy is not null
				&& valTy is PointerTypeSymbol ptrTy
				&& val.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind
				&& !paramTy.Equals(valTy)
				&& paramTy is not PointerTypeSymbol)
			{
				val = Builder.BuildLoad2(codegen.Types.Lower(ptrTy.ReferencedType), val, "deref_arg");
				valTy = ptrTy.ReferencedType;
			}

			if (paramTy is not null
				&& argExpr is IdentifierExpressionSyntax moveArg
				&& !Function.DisposedVars.Contains(moveArg.Name)
				&& Function.VariableTypes.TryGetValue(moveArg.Name, out var srcTy)
				&& srcTy is UnionTypeSymbol srcUnion
				&& cleanup.UnionNeedsTagCheckedCleanup(srcUnion)
				&& paramTy is UnionTypeSymbol paramUnion
				&& paramUnion.Name == srcUnion.Name)
			{
				Function.MovedVars.Add(moveArg.Name);
			}

			var isVariadic = (astExterns.TryGetValue(emitName, out var ext) && ext.IsVariadic)
				|| (astExternBlockFunctions.TryGetValue(emitName, out var blockFn) && blockFn.IsVariadic);
			var declaredParamCount = codegen.FunctionParameterTypes.TryGetValue(emitName, out var fpt) ? fpt.Count : 0;
			var isVariadicArg = isVariadic && (i + actualParamOffset >= declaredParamCount);

			if (isVariadicArg)
			{
				if (valTy.Equals(TypeSymbol.Bool))
					val = Builder.BuildZExt(val, LLVMTypeRef.Int32, "prom_bool");
				else if (valTy.Equals(TypeSymbol.Char))
					val = Builder.BuildZExt(val, LLVMTypeRef.Int32, "prom_char");
			}
			else if (paramTy is not null)
			{
				if (TypeSymbol.IsFloatingPointType(paramTy) && TypeSymbol.IsIntegerType(valTy))
				{
					val = TypeSymbol.IsSignedIntegerType(valTy)
						? Builder.BuildSIToFP(val, codegen.Types.Lower(paramTy), "call_sitofp")
						: Builder.BuildUIToFP(val, codegen.Types.Lower(paramTy), "call_uitofp");
					valTy = paramTy;
				}
				else if (TypeSymbol.IsIntegerType(valTy) && TypeSymbol.IsIntegerType(paramTy))
				{
					val = coercion.CoerceIntegerWidth(val, valTy, paramTy);
					valTy = paramTy;
				}

				var actualParamIndex = (uint)(i + actualParamOffset);
				if (actualParamIndex < callee.ParamsCount)
				{
					var expectedLlvmTy = callee.GetParam(actualParamIndex).TypeOf;
					if (expectedLlvmTy.Kind == LLVMTypeKind.LLVMIntegerTypeKind
						&& val.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
					{
						if (val.TypeOf.IntWidth < expectedLlvmTy.IntWidth)
						{
							val = TypeSymbol.IsSignedIntegerType(valTy)
								? Builder.BuildSExt(val, expectedLlvmTy, "call_sext")
								: Builder.BuildZExt(val, expectedLlvmTy, "call_zext");
						}
						else if (val.TypeOf.IntWidth > expectedLlvmTy.IntWidth)
						{
							val = Builder.BuildTrunc(val, expectedLlvmTy, "call_trunc");
						}
					}
				}
			}

			if (paramTy is not null && paramTy.Equals(TypeSymbol.String) && valTy is ArrayTypeSymbol)
			{
				LLVMValueRef arrayPtr;
				if (argExpr is IdentifierExpressionSyntax id)
					arrayPtr = Function.Locals[id.Name];
				else
					(arrayPtr, _, _, _) = aggregates.GetFieldPointer(argExpr);

				val = Builder.BuildBitCast(arrayPtr, codegen.Types.Lower(TypeSymbol.String), "array_to_string_cast");
			}
			else if (paramTy is not null && paramTy.Equals(TypeSymbol.String) && valTy is SliceTypeSymbol)
			{
				LLVMValueRef slicePtr;
				if (argExpr is IdentifierExpressionSyntax id)
					slicePtr = Function.Locals[id.Name];
				else
					(slicePtr, _, _, _) = aggregates.GetFieldPointer(argExpr);

				var sliceLayout = codegen.Types.Lower(valTy);
				var ptrField = Builder.BuildGEP2(
					sliceLayout,
					slicePtr,
					new LLVMValueRef[] {
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
					},
					"slice_ptr_field");
				val = Builder.BuildLoad2(codegen.Types.Lower(TypeSymbol.String), ptrField, "slice_to_string_cast");
			}

			args.Add(val);
		}

		var retTypeSymbol = codegen.FunctionReturnTypes.TryGetValue(emitName, out var ret)
			? ret
			: (resolvedFunc is { ReturnType: not null } resolvedFn ? resolvedFn.ReturnType : TypeSymbol.Int);
		var instName = retTypeSymbol.Equals(TypeSymbol.Void) ? "" : "call_val";

		return Builder.BuildCall2(funcType, callee, args.ToArray(), instName);
	}

	/// <summary>
	/// Loads a two-word safe delegate value and emits the uniform thunk call
	/// <c>invoke(context, arguments...)</c>.
	/// </summary>
	private LLVMValueRef EmitDelegateInvocation(CallExpressionSyntax call, DelegateTypeSymbol delegateType)
	{
		var i8PtrTy = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
		var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
		var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1);

		LLVMValueRef delegatePtr;
		if (call.FunctionName.Contains('.'))
		{
			var lastDot = call.FunctionName.LastIndexOf('.');
			var receiverName = call.FunctionName[..lastDot];
			var memberName = call.FunctionName[(lastDot + 1)..];

			if (TryResolveReceiverPointer(receiverName, out var recvPtr, out var recvTy)
				&& (recvTy is PointerTypeSymbol rpp && rpp.ReferencedType is StructTypeSymbol rstruct
					? rstruct
					: recvTy as StructTypeSymbol) is { } receiverStruct)
			{
				LLVMValueRef basePtr = recvPtr;
				if (recvTy is PointerTypeSymbol)
					basePtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), recvPtr, "receiver_loaded_ptr");
				var fieldIdx = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)codegen.AggregateLayout.GetFieldIndex(receiverStruct, memberName));
				delegatePtr = Builder.BuildGEP2(codegen.Types.Lower(receiverStruct), basePtr, new LLVMValueRef[] { zero, fieldIdx }, "delegate_field_ptr");
			}
			else
			{
				throw new InvalidOperationException($"Cannot resolve receiver '{receiverName}' for delegate invocation '{call.FunctionName}'.");
			}
		}
		else
		{
			var calleeName = ResolveGlobalKey(call.FunctionName);
			if (Function.Locals.TryGetValue(call.FunctionName, out var localSlot))
			{
				delegatePtr = localSlot;
			}
			else if (calleeName is { } key && codegen.GlobalVariables.TryGetValue(key, out var globalSlot))
			{
				delegatePtr = globalSlot;
			}
			else if (Function.Locals.TryGetValue("this", out var thisPtr)
				&& Function.VariableTypes["this"] is PointerTypeSymbol thisPtrTy
				&& thisPtrTy.ReferencedType is StructTypeSymbol thisStruct
				&& thisStruct.FindField(call.FunctionName) is { } thisField
				&& thisField.Type is DelegateTypeSymbol)
			{
				var actualThisPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisPtr, "loaded_this_ptr");
				var fieldIdx = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)codegen.AggregateLayout.GetFieldIndex(thisStruct, call.FunctionName));
				delegatePtr = Builder.BuildGEP2(codegen.Types.Lower(thisStruct), actualThisPtr, new LLVMValueRef[] { zero, fieldIdx }, "delegate_field_ptr");
			}
			else
			{
				throw new InvalidOperationException($"Cannot resolve delegate variable '{call.FunctionName}'.");
			}
		}

		var wordTy = codegen.Types.Lower(delegateType);
		var tmp = Builder.BuildAlloca(wordTy, "$del_tmp");
		Builder.BuildStore(Builder.BuildLoad2(wordTy, delegatePtr, "del_val"), tmp);

		var invokeSlot = Builder.BuildGEP2(wordTy, tmp, new LLVMValueRef[] { zero, zero }, "invoke_slot");
		var ctxSlot = Builder.BuildGEP2(wordTy, tmp, new LLVMValueRef[] { zero, one }, "ctx_slot");
		var invokeWord = Builder.BuildLoad2(i8PtrTy, invokeSlot, "invoke_word");
		var contextWord = Builder.BuildLoad2(i8PtrTy, ctxSlot, "ctx_word");

		var thunkParamTys = new List<LLVMTypeRef> { i8PtrTy };
		foreach (var parameter in delegateType.Parameters)
			thunkParamTys.Add(codegen.Types.Lower(parameter.Type));
		var thunkTy = LLVMTypeRef.CreateFunction(codegen.Types.Lower(delegateType.ReturnType), [.. thunkParamTys]);
		var invokeFn = Builder.BuildPointerCast(invokeWord, LLVMTypeRef.CreatePointer(thunkTy, 0), "invoke_fn");

		var args = new List<LLVMValueRef> { contextWord };
		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var argExpr = call.Arguments[i];
			var argVal = emitExpression(argExpr);
			if (i < delegateType.Parameters.Count)
				argVal = coercion.CoerceIntegerWidth(argVal, expressionTypes.Resolve(argExpr), delegateType.Parameters[i].Type);
			args.Add(argVal);
		}

		return Builder.BuildCall2(
			thunkTy,
			invokeFn,
			args.ToArray(),
			delegateType.ReturnType.Equals(TypeSymbol.Void) ? "" : "$del_call");
	}

	/// <summary>
	/// Lowers a function decorated with <c>Intrinsic</c> to the corresponding LLVM intrinsic,
	/// including the hidden operands required by selected LLVM intrinsic signatures.
	/// </summary>
	private LLVMValueRef EmitIntrinsicCall(CallExpressionSyntax call, FunctionSymbol func)
	{
		var args = call.Arguments.Select(emitExpression).ToList();

		if (args.Count == func.Parameters.Count)
		{
			for (var i = 0; i < args.Count; i++)
			{
				var paramTy = func.Parameters[i].Type;
				var argTy = expressionTypes.Resolve(call.Arguments[i]);
				if (TypeSymbol.IsIntegerType(argTy) && TypeSymbol.IsIntegerType(paramTy))
				{
					args[i] = coercion.CoerceIntegerWidth(args[i], argTy, paramTy);
				}
				else if (TypeSymbol.IsIntegerType(argTy) && TypeSymbol.IsFloatingPointType(paramTy))
				{
					args[i] = TypeSymbol.IsSignedIntegerType(argTy)
						? Builder.BuildSIToFP(args[i], codegen.Types.Lower(paramTy), "intr_sitofp")
						: Builder.BuildUIToFP(args[i], codegen.Types.Lower(paramTy), "intr_uitofp");
				}
			}
		}

		var baseName = func.IntrinsicName!;
		var injected = new List<LLVMValueRef>();
		switch (baseName)
		{
			case "abs":
				injected.Add(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0));
				break;
			case "ctlz":
			case "cttz":
				injected.Add(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0));
				break;
			case "rotl":
			case "rotr":
				{
					var rotateAmount = args[^1];
					var valueType = args[0].TypeOf;
					var shiftType = rotateAmount.TypeOf;
					if (shiftType.Kind != LLVMTypeKind.LLVMIntegerTypeKind || shiftType.IntWidth != valueType.IntWidth)
					{
						rotateAmount = valueType.IntWidth > shiftType.IntWidth
							? Builder.BuildZExt(rotateAmount, valueType, "rot_zext")
							: Builder.BuildTrunc(rotateAmount, valueType, "rot_trunc");
					}

					var widthMask = LLVMValueRef.CreateConstInt(valueType, (ulong)valueType.IntWidth - 1);
					rotateAmount = Builder.BuildAnd(rotateAmount, widthMask, "rot_mask");
					args = new List<LLVMValueRef> { args[0], args[0], rotateAmount };
					baseName = baseName == "rotl" ? "fshl" : "fshr";
					break;
				}
			case "fpc.nan":
			case "fpc.inf":
			case "fpc.finite":
			case "fpc.normal":
			case "fpc.subnormal":
			case "fpc.zero":
			case "fpc.negzero":
			case "fpc.neg":
				injected.Add(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, FpClassMask(baseName)));
				baseName = "is.fpclass";
				break;
		}

		string targetName;
		if (baseName.StartsWith("llvm.", StringComparison.Ordinal))
		{
			targetName = baseName;
		}
		else
		{
			var typeSuffix = args.Count > 0 ? GetTypeSuffix(args[0].TypeOf) : "";
			targetName = string.IsNullOrEmpty(typeSuffix)
				? $"llvm.{baseName}"
				: $"llvm.{baseName}.{typeSuffix}";
		}

		var emittedArgs = args.Concat(injected).ToArray();
		var retTy = codegen.Types.Lower(func.ReturnType);
		var callee = GetOrDeclareIntrinsic(targetName, emittedArgs, retTy);
		var funcType = codegen.FunctionTypes[targetName];
		var callName = retTy.Kind == LLVMTypeKind.LLVMVoidTypeKind ? "" : "intrinsic_call";
		return Builder.BuildCall2(funcType, callee, emittedArgs, callName);
	}

	/// <summary>
	/// Resolves method/delegate receiver storage using the same local, global, and implicit-this
	/// lookup order used by the pre-refactor call path.
	/// </summary>
	private bool TryResolveReceiverPointer(string receiverName, out LLVMValueRef receiverPtr, out TypeSymbol receiverType)
	{
		receiverPtr = default;
		if (Function.Locals.TryGetValue(receiverName, out var localPtr))
		{
			receiverPtr = localPtr;
			receiverType = Function.VariableTypes[receiverName];
			return true;
		}

		if ((codegen.GlobalVariables.ContainsKey(receiverName) ? receiverName : ResolveGlobalKey(receiverName)) is { } recvKey
			&& codegen.GlobalVariables.TryGetValue(recvKey, out var globalPtr))
		{
			receiverPtr = globalPtr;
			receiverType = codegen.GlobalVariableTypes[recvKey];
			return true;
		}

		if (Function.Locals.TryGetValue("this", out var thisPtr)
			&& Function.VariableTypes["this"] is PointerTypeSymbol thisTy
			&& thisTy.ReferencedType is StructTypeSymbol selfStruct
			&& selfStruct.FindField(receiverName) is { } field)
		{
			var actualThisPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisPtr, "loaded_this_ptr");
			var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
			var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)codegen.AggregateLayout.GetFieldIndex(selfStruct, receiverName));
			receiverPtr = Builder.BuildGEP2(codegen.Types.Lower(selfStruct), actualThisPtr, new LLVMValueRef[] { zero, index }, "this_field_ptr");
			receiverType = field.Type;
			return true;
		}

		receiverType = null!;
		return false;
	}

	private TypeSymbol? GetParamType(string mangledFuncName, int index)
	{
		return codegen.FunctionParameterTypes.TryGetValue(mangledFuncName, out var paramTypes) && index < paramTypes.Count
			? paramTypes[index]
			: null;
	}

	/// <summary>
	/// Resolves an unqualified source function name to the module registry key visible from the
	/// active compilation unit.
	/// </summary>
	private string ResolveFunctionName(string name, CompilationUnitSyntax activeUnit)
	{
		if (name == "main" || name == "Main")
			return "main";

		if (codegen.Globals.ContainsKey(name) || BindingContext.GenericFunctionTemplates.ContainsKey(name))
			return name;

		var ns = activeUnit.NamespaceDeclaration?.Name;
		var localMangled = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
		if (codegen.Globals.ContainsKey(localMangled) || BindingContext.GenericFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		foreach (var importNs in BindingContext.GetActiveUsings(activeUnit))
		{
			var candidateMangled = $"{importNs}.{name}";
			if (codegen.Globals.ContainsKey(candidateMangled) || BindingContext.GenericFunctionTemplates.ContainsKey(candidateMangled))
				return candidateMangled;
		}

		return name;
	}

	/// <summary>
	/// Resolves a short global name to a unique qualified key using the current namespace and
	/// active using directives.
	/// </summary>
	private string? ResolveGlobalKey(string shortName)
	{
		if (!codegen.GlobalShortNames.TryGetValue(shortName, out var candidates))
			return null;

		if (candidates.Count == 1)
			return candidates[0];

		var currentNs = BindingContext.CurrentNamespace;
		if (!string.IsNullOrEmpty(currentNs))
		{
			var own = $"{currentNs}.{shortName}";
			if (candidates.Contains(own))
				return own;
		}

		foreach (var ns in BindingContext.GetActiveUsings(BindingContext.CurrentUnit))
		{
			var viaKey = $"{ns}.{shortName}";
			if (candidates.Contains(viaKey))
				return viaKey;
		}

		return null;
	}

	/// <summary>
	/// Reuses an already declared LLVM intrinsic or declares the exact signature required by the
	/// emitted operands.
	/// </summary>
	private LLVMValueRef GetOrDeclareIntrinsic(
		string intrinsicBaseName,
		IReadOnlyList<LLVMValueRef> args,
		LLVMTypeRef returnType)
	{
		var fullIntrinsicName = intrinsicBaseName;
		if (args.Count > 0 && !intrinsicBaseName.EndsWith(".f64") && !intrinsicBaseName.EndsWith(".f32"))
		{
			if (args[0].TypeOf.Kind == LLVMTypeKind.LLVMDoubleTypeKind)
				fullIntrinsicName = $"{intrinsicBaseName}.f64";
			else if (args[0].TypeOf.Kind == LLVMTypeKind.LLVMFloatTypeKind)
				fullIntrinsicName = $"{intrinsicBaseName}.f32";
		}

		if (codegen.Globals.TryGetValue(fullIntrinsicName, out var existing))
			return existing;

		var paramTypes = args.Select(arg => arg.TypeOf).ToArray();
		var funcType = LLVMTypeRef.CreateFunction(returnType, paramTypes);
		var func = codegen.Module.AddFunction(fullIntrinsicName, funcType);
		codegen.Globals[fullIntrinsicName] = func;
		codegen.FunctionTypes[fullIntrinsicName] = funcType;
		return func;
	}

	/// <summary>
	/// Returns the enum type carried by a binder-synthesized instance-call receiver parameter.
	/// Synthetic enum methods bind their <c>this</c> parameter as a pointer to the exact
	/// <see cref="EnumTypeSymbol"/>, which is more authoritative than reconstructing the type from
	/// the function-local storage table during LLVM emission.
	/// </summary>
	private static EnumTypeSymbol? GetBoundEnumReceiverType(FunctionSymbol function)
	{
		if (function.Parameters.Count == 0)
			return null;

		var receiverType = function.Parameters[0].Type;
		if (receiverType is PointerTypeSymbol pointerType)
			receiverType = pointerType.ReferencedType;

		return receiverType as EnumTypeSymbol;
	}

	/// <summary>
	/// Maps a floating-point classification intrinsic name to the LLVM is.fpclass bit mask
	/// expected by the synthesized intrinsic call.
	/// </summary>
	private static ulong FpClassMask(string baseName) => baseName switch
	{
		"fpc.nan" => 1 | 2,
		"fpc.inf" => 4 | 512,
		"fpc.finite" => 32 | 64 | 16 | 128 | 8 | 256,
		"fpc.normal" => 8 | 256,
		"fpc.subnormal" => 16 | 128,
		"fpc.zero" => 32 | 64,
		"fpc.negzero" => 32,
		"fpc.neg" => 4 | 8 | 16 | 32,
		_ => 0,
	};

	/// <summary>
	/// Returns the LLVM intrinsic suffix for the scalar type used by the current intrinsic call.
	/// Unsupported types intentionally produce an empty suffix to preserve the existing lowering behavior.
	/// </summary>
	private static string GetTypeSuffix(LLVMTypeRef type) => type.Kind switch
	{
		LLVMTypeKind.LLVMDoubleTypeKind => "f64",
		LLVMTypeKind.LLVMFloatTypeKind => "f32",
		LLVMTypeKind.LLVMIntegerTypeKind when type.IntWidth == 64 => "i64",
		LLVMTypeKind.LLVMIntegerTypeKind when type.IntWidth == 32 => "i32",
		LLVMTypeKind.LLVMIntegerTypeKind when type.IntWidth == 16 => "i16",
		LLVMTypeKind.LLVMIntegerTypeKind when type.IntWidth == 8 => "i8",
		_ => "",
	};

}
