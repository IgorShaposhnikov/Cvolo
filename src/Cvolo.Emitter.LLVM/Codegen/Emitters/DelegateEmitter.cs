using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Emitter.LLVM.Codegen.Values;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits safe delegate values, closure environments, and synthetic invoke thunks.
/// </summary>
/// <remarks>
/// Safe delegates use the existing two-word representation <c>{ invoke, context }</c>. This
/// emitter owns construction of that representation and the synthetic functions that adapt
/// lambdas or function groups to the uniform thunk signature. Delegate invocation remains in
/// <see cref="CallEmitter"/>, while destruction of captured values remains in
/// <see cref="CleanupEmitter"/>.
///
/// Expression and statement emission remain delegated through the existing orchestration seam,
/// while semantic expression typing and named-value access are shared through
/// <see cref="ExpressionTypeResolver"/> and <see cref="ValueLoader"/>. This extraction does not
/// introduce new delegate semantics.
/// </remarks>
/// <remarks>
/// Creates a delegate emitter backed by module-level codegen state and callbacks into the
/// still-centralized expression/function emission paths.
/// </remarks>
internal sealed class DelegateEmitter(
	CodegenContext codegen,
	Func<FunctionCodegenContext> getFunction,
	Action<FunctionCodegenContext> setFunction,
	Func<ExpressionSyntax, LLVMValueRef> emitExpression,
	Action<BlockStatementSyntax> emitBlock,
	ExpressionTypeResolver expressionTypes,
	ValueCoercion coercion,
	ValueLoader values)
{
	private int _delegateFunctionCounter;

	private LLVMBuilderRef Builder => codegen.Builder;
	private LLVMModuleRef Module => codegen.Module;
	private FunctionCodegenContext Function => getFunction();
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Delegate emission requires an active binding context.");


	/// <summary>
	/// Emits a lambda as a safe delegate by materializing its capture environment and generating a
	/// uniform thunk whose hidden first parameter points at that environment.
	/// </summary>
	public LLVMValueRef EmitLambdaExpression(LambdaExpressionSyntax lambda)
	{
		var info = BindingContext.ResolvedLambdas[lambda];
		var delegateType = info.Delegate;
		var captures = ComputeLambdaCaptures(lambda);
		var i8PtrTy = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

		LLVMValueRef? envAlloc = null;
		LLVMTypeRef? envTy = null;
		if (captures.Count > 0)
		{
			var fieldTypes = new List<LLVMTypeRef>();
			foreach (var (_, type) in captures)
			{
				fieldTypes.Add(info.CaptureMode == LambdaCaptureMode.Ref ? i8PtrTy : codegen.Types.Lower(type));
			}

			envTy = LLVMTypeRef.CreateStruct([.. fieldTypes], false);
			envAlloc = Builder.BuildAlloca(envTy.Value, "$lambda_env");
			var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
			for (var i = 0; i < captures.Count; i++)
			{
				var (captureName, captureType) = captures[i];
				var fieldPtr = Builder.BuildGEP2(
					envTy.Value,
					envAlloc.Value,
					new LLVMValueRef[] { zero, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)i) },
					"$lambda_cap");

				if (info.CaptureMode == LambdaCaptureMode.Ref)
				{
					Builder.BuildStore(Builder.BuildPointerCast(Function.Locals[captureName], i8PtrTy, "cap_ref"), fieldPtr);
				}
				else
				{
					Builder.BuildStore(values.Load(captureName), fieldPtr);
					if (info.CaptureMode == LambdaCaptureMode.Move)
						Function.MovedVars.Add(captureName);
				}
			}
		}

		var thunkName = $"$lambda.{++_delegateFunctionCounter}";
		var visibleParams = new List<(string Name, TypeSymbol Type)>();
		for (var i = 0; i < lambda.Parameters.Count && i < delegateType.Parameters.Count; i++)
		{
			visibleParams.Add((lambda.Parameters[i].Name, delegateType.Parameters[i].Type));
		}

		var thunk = CreateDelegateThunk(thunkName, delegateType.ReturnType, visibleParams, contextParameter =>
		{
			if (captures.Count > 0 && envTy is { } environmentType)
			{
				var envPtr = Builder.BuildPointerCast(
					contextParameter,
					LLVMTypeRef.CreatePointer(environmentType, 0),
					"lambda_ctx");
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);

				for (var i = 0; i < captures.Count; i++)
				{
					var (captureName, captureType) = captures[i];
					var fieldPtr = Builder.BuildGEP2(
						environmentType,
						envPtr,
						new LLVMValueRef[] { zero, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)i) },
						"$lambda_cap_ptr");

					if (info.CaptureMode == LambdaCaptureMode.Ref)
					{
						var slot = Builder.BuildLoad2(i8PtrTy, fieldPtr, "cap_ref_slot");
						Function.Locals[captureName] = Builder.BuildPointerCast(
							slot,
							LLVMTypeRef.CreatePointer(codegen.Types.Lower(captureType), 0),
							"cap_ref_local");
					}
					else
					{
						Function.Locals[captureName] = fieldPtr;
					}

					Function.VariableTypes[captureName] = captureType;
				}
			}

			if (info.BodyIsValueExpression && lambda.ExpressionBody is not null)
			{
				var bodyValue = emitExpression(lambda.ExpressionBody);
				if (delegateType.ReturnType.Equals(TypeSymbol.Void))
				{
					Builder.BuildRetVoid();
				}
				else
				{
					Builder.BuildRet(coercion.CoerceIntegerWidth(
						bodyValue,
						expressionTypes.Resolve(lambda.ExpressionBody),
						delegateType.ReturnType));
				}
			}
			else if (lambda.BlockBody is not null)
			{
				emitBlock(lambda.BlockBody);
				if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
					Builder.BuildRetVoid();
			}
			else
			{
				Builder.BuildRetVoid();
			}
		});

		var contextValue = envAlloc is { } environment
			? Builder.BuildPointerCast(environment, i8PtrTy, "lambda_env_ptr")
			: LLVMValueRef.CreateConstPointerNull(i8PtrTy);

		return BuildDelegateValue(thunk, contextValue, delegateType);
	}

	/// <summary>
	/// Converts a resolved function group or bound method group into the uniform safe-delegate
	/// representation, capturing receiver storage in the delegate context when required.
	/// </summary>
	public LLVMValueRef EmitFunctionGroupConversion(ExpressionSyntax expression, FunctionSymbol function)
	{
		var isBound = expression is MemberAccessExpressionSyntax
			|| (function.Parameters.Count > 0 && function.Parameters[0].Name == "this");
		var i8PtrTy = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

		LLVMValueRef? receiverStorage = null;
		TypeSymbol? receiverType = null;
		if (expression is MemberAccessExpressionSyntax memberAccess)
		{
			if (TryResolveReceiverPointer(GetIdentifierName(memberAccess.Expression) ?? "", out var receiverPtr, out var resolvedReceiverType))
			{
				receiverStorage = receiverPtr;
				receiverType = resolvedReceiverType;
			}
		}
		else if (isBound && Function.Locals.TryGetValue("this", out var thisPtr))
		{
			receiverStorage = Builder.BuildLoad2(i8PtrTy, thisPtr, "loaded_this_ptr");
			receiverType = (Function.VariableTypes["this"] as PointerTypeSymbol)?.ReferencedType;
		}

		var start = isBound ? 1 : 0;
		var visibleParams = new List<(string Name, TypeSymbol Type)>();
		for (var i = start; i < function.Parameters.Count; i++)
		{
			visibleParams.Add((function.Parameters[i].Name, function.Parameters[i].Type));
		}

		var thunkName = $"$group.{++_delegateFunctionCounter}";
		var thunk = CreateDelegateThunk(thunkName, function.ReturnType, visibleParams, contextParameter =>
		{
			var callArguments = new List<LLVMValueRef>();
			if (isBound)
			{
				LLVMValueRef receiverArgument = contextParameter;
				if (receiverType is PointerTypeSymbol)
				{
					receiverArgument = Builder.BuildLoad2(i8PtrTy, contextParameter, "recv_load");
				}

				callArguments.Add(receiverArgument);
			}

			for (var i = start; i < function.Parameters.Count; i++)
			{
				var local = Function.Locals[visibleParams[i - start].Name];
				callArguments.Add(Builder.BuildLoad2(codegen.Types.Lower(function.Parameters[i].Type), local, "group_arg"));
			}

			var instructionName = function.ReturnType.Equals(TypeSymbol.Void) ? "" : "group_call";
			var callValue = Builder.BuildCall2(
				codegen.FunctionTypes[function.Name],
				codegen.Globals[function.Name],
				callArguments.ToArray(),
				instructionName);

			if (function.ReturnType.Equals(TypeSymbol.Void))
				Builder.BuildRetVoid();
			else
				Builder.BuildRet(callValue);
		});

		var contextValue = receiverStorage is { } storage
			? Builder.BuildPointerCast(storage, i8PtrTy, "receiver_ctx")
			: LLVMValueRef.CreateConstPointerNull(i8PtrTy);

		return BuildDelegateValue(thunk, contextValue, expressionTypes.BuildGroupDelegateType(function, isBound));
	}

	/// <summary>
	/// Walks an AST subtree and yields every node of the requested syntax type.
	/// </summary>
	private static IEnumerable<TSyntax> EnumerateNodes<TSyntax>(SyntaxNode? root)
		where TSyntax : SyntaxNode
	{
		if (root is null)
			yield break;
		if (root is TSyntax match)
			yield return match;

		foreach (var child in root.GetChildren())
		{
			foreach (var nested in EnumerateNodes<TSyntax>(child))
			{
				yield return nested;
			}
		}
	}

	/// <summary>
	/// Computes the function-local values referenced by a lambda body that must be captured in its
	/// closure environment. Globals, <c>this</c>, and the lambda's own parameters are excluded.
	/// </summary>
	private List<(string Name, TypeSymbol Type)> ComputeLambdaCaptures(LambdaExpressionSyntax lambda)
	{
		var result = new List<(string Name, TypeSymbol Type)>();
		var seen = new HashSet<string>();
		var parameterNames = new HashSet<string>(lambda.Parameters.Select(parameter => parameter.Name));

		var root = lambda.ExpressionBody is not null ? (SyntaxNode)lambda.ExpressionBody : lambda.BlockBody;
		foreach (var identifier in EnumerateNodes<IdentifierExpressionSyntax>(root))
		{
			var name = identifier.Name;
			if (!seen.Add(name))
				continue;
			if (name == "this" || parameterNames.Contains(name))
				continue;
			if (codegen.GlobalShortNames.ContainsKey(name) || codegen.GlobalVariables.ContainsKey(name))
				continue;
			if (Function.VariableTypes.TryGetValue(name, out var type))
				result.Add((name, type));
		}

		return result;
	}

	/// <summary>
	/// Resolves storage for a bound-method receiver from locals, globals, or a field of the current
	/// implicit <c>this</c> receiver.
	/// </summary>
	private bool TryResolveReceiverPointer(string receiverName, out LLVMValueRef receiverPointer, out TypeSymbol receiverType)
	{
		receiverPointer = default;
		if (Function.Locals.TryGetValue(receiverName, out var localPointer))
		{
			receiverPointer = localPointer;
			receiverType = Function.VariableTypes[receiverName];
			return true;
		}

		if ((codegen.GlobalVariables.ContainsKey(receiverName) ? receiverName : values.ResolveGlobalKey(receiverName)) is { } receiverKey
			&& codegen.GlobalVariables.TryGetValue(receiverKey, out var globalPointer))
		{
			receiverPointer = globalPointer;
			receiverType = codegen.GlobalVariableTypes[receiverKey];
			return true;
		}

		if (Function.Locals.TryGetValue("this", out var thisPointer)
			&& Function.VariableTypes["this"] is PointerTypeSymbol thisType
			&& thisType.ReferencedType is StructTypeSymbol selfStruct
			&& selfStruct.FindField(receiverName) is { } field)
		{
			var actualThisPointer = Builder.BuildLoad2(
				LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
				thisPointer,
				"loaded_this_ptr");
			var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
			var index = LLVMValueRef.CreateConstInt(
				LLVMTypeRef.Int32,
				(uint)codegen.AggregateLayout.GetFieldIndex(selfStruct, receiverName));
			receiverPointer = Builder.BuildGEP2(
				codegen.Types.Lower(selfStruct),
				actualThisPointer,
				new LLVMValueRef[] { zero, index },
				"this_field_ptr");
			receiverType = field.Type;
			return true;
		}

		receiverType = null!;
		return false;
	}

	/// <summary>
	/// Materializes the two-word safe-delegate value containing the invoke-thunk pointer and its
	/// opaque context pointer.
	/// </summary>
	private LLVMValueRef BuildDelegateValue(LLVMValueRef invokeFunction, LLVMValueRef contextValue, DelegateTypeSymbol delegateType)
	{
		var delegateLayout = codegen.Types.Lower(delegateType);
		var slot = Builder.BuildAlloca(delegateLayout, "$delegate_slot");
		var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
		var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1);
		var i8PtrTy = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

		var invokeSlot = Builder.BuildGEP2(
			delegateLayout,
			slot,
			new LLVMValueRef[] { zero, zero },
			"delegate_invoke_slot");
		Builder.BuildStore(Builder.BuildPointerCast(invokeFunction, i8PtrTy, "invoke_ptr"), invokeSlot);

		var contextSlot = Builder.BuildGEP2(
			delegateLayout,
			slot,
			new LLVMValueRef[] { zero, one },
			"delegate_ctx_slot");
		Builder.BuildStore(contextValue, contextSlot);

		return Builder.BuildLoad2(delegateLayout, slot, "delegate_val");
	}

	/// <summary>
	/// Creates a synthetic uniform thunk <c>R(void* context, P...)</c>, temporarily switches to a
	/// fresh function-local codegen context while emitting its body, then restores the enclosing
	/// function and LLVM builder insertion point.
	/// </summary>
	private LLVMValueRef CreateDelegateThunk(
		string thunkName,
		TypeSymbol returnType,
		IReadOnlyList<(string Name, TypeSymbol Type)> visibleParameters,
		Action<LLVMValueRef> emitBody)
	{
		var i8PtrTy = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
		var parameterTypes = new List<LLVMTypeRef> { i8PtrTy };
		foreach (var (_, type) in visibleParameters)
		{
			parameterTypes.Add(codegen.Types.Lower(type));
		}

		var functionType = LLVMTypeRef.CreateFunction(codegen.Types.Lower(returnType), [.. parameterTypes]);
		var function = Module.AddFunction(thunkName, functionType);
		codegen.Globals[thunkName] = function;
		codegen.FunctionTypes[thunkName] = functionType;
		codegen.FunctionReturnTypes[thunkName] = returnType;
		codegen.FunctionParameterTypes[thunkName] = [.. visibleParameters.Select(parameter => parameter.Type).ToList()];

		var savedBlock = Builder.InsertBlock;
		var savedFunction = Function;
		setFunction(new FunctionCodegenContext());

		foreach (var global in codegen.GlobalVariables)
		{
			if (!Function.Locals.ContainsKey(global.Key))
			{
				Function.Locals[global.Key] = global.Value;
			}
		}

		foreach (var globalType in codegen.GlobalVariableTypes)
		{
			if (!Function.VariableTypes.ContainsKey(globalType.Key))
			{
				Function.VariableTypes[globalType.Key] = globalType.Value;
			}
		}

		foreach (var (shortName, _) in codegen.GlobalShortNames)
		{
			if (Function.Locals.ContainsKey(shortName))
				continue;
			if (values.ResolveGlobalKey(shortName) is { } key && codegen.GlobalVariables.TryGetValue(key, out var storage))
			{
				Function.Locals[shortName] = storage;
				Function.VariableTypes[shortName] = codegen.GlobalVariableTypes[key];
			}
		}

		var entry = function.AppendBasicBlock("entry");
		Builder.PositionAtEnd(entry);

		for (var i = 0; i < visibleParameters.Count; i++)
		{
			var parameterName = visibleParameters[i].Name;
			var parameterType = codegen.Types.Lower(visibleParameters[i].Type);
			var alloca = Builder.BuildAlloca(parameterType, parameterName);
			Builder.BuildStore(function.GetParam((uint)(i + 1)), alloca);
			Function.Locals[parameterName] = alloca;
			Function.VariableTypes[parameterName] = visibleParameters[i].Type;
		}

		emitBody(function.GetParam(0));

		Builder.PositionAtEnd(savedBlock);
		setFunction(savedFunction);

		return function;
	}

	/// <summary>
	/// Extracts the left-most identifier from an identifier/member-access chain so bound-method
	/// conversion can resolve the receiver storage using the existing name-based lookup rules.
	/// </summary>
	private static string? GetIdentifierName(ExpressionSyntax expression)
	{
		return expression switch
		{
			IdentifierExpressionSyntax identifier => identifier.Name,
			MemberAccessExpressionSyntax memberAccess => GetIdentifierName(memberAccess.Expression),
			_ => null,
		};
	}
}
