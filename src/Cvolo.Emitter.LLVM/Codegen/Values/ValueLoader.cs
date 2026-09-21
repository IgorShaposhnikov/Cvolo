using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Emitter.LLVM.Codegen.Emitters;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Values;

/// <summary>
/// Resolves named values visible to the active function and emits the LLVM loads required to read
/// their current value.
/// </summary>
/// <remarks>
/// This helper centralizes the legacy local/global/implicit-receiver lookup rules that were
/// previously implemented in <c>CodeGenerator.Load</c> and <c>ResolveGlobalKey</c>. It does not
/// perform general expression lowering or decide whether a source-level name is semantically valid;
/// binding and validation remain responsible for those decisions.
/// </remarks>
internal sealed class ValueLoader(
	CodegenContext codegen,
	AggregateEmitter aggregates,
	Func<FunctionCodegenContext> getFunction)
{
	private LLVMBuilderRef Builder => codegen.Builder;
	private FunctionCodegenContext Function => getFunction();

	/// <summary>
	/// Resolves an unqualified global name to the visible fully qualified global key.
	/// </summary>
	/// <remarks>
	/// A globally unique short name resolves directly. Ambiguous names prefer the current namespace
	/// and then active using directives, preserving the previous code-generation lookup order.
	/// </remarks>
	/// <param name="shortName">Unqualified source-level global name.</param>
	/// <returns>The qualified global key, or <see langword="null"/> when no unique visible key exists.</returns>
	public string? ResolveGlobalKey(string shortName)
	{
		if (!codegen.GlobalShortNames.TryGetValue(shortName, out var candidates))
			return null;

		if (candidates.Count == 1)
			return candidates[0];

		var bindingContext = codegen.BindingContext;
		var currentNamespace = bindingContext?.CurrentNamespace;
		if (!string.IsNullOrEmpty(currentNamespace))
		{
			var own = $"{currentNamespace}.{shortName}";
			if (candidates.Contains(own))
				return own;
		}

		foreach (var importedNamespace in bindingContext?.GetActiveUsings(bindingContext.CurrentUnit) ?? [])
		{
			var candidate = $"{importedNamespace}.{shortName}";
			if (candidates.Contains(candidate))
				return candidate;
		}

		return null;
	}

	/// <summary>
	/// Emits the read of a named local/global value, including the existing implicit <c>this</c>
	/// field and enum-variant lookup behavior used inside extension bodies.
	/// </summary>
	/// <param name="name">Visible source-level value name.</param>
	/// <returns>The LLVM value produced by the current representation-specific load path.</returns>
	/// <exception cref="InvalidOperationException">Thrown when no visible value can be resolved.</exception>
	public LLVMValueRef Load(string name)
	{
		if (!Function.Locals.TryGetValue(name, out var storage))
		{
			if (Function.Locals.TryGetValue("this", out var thisStorage))
			{
				if (Function.VariableTypes["this"] is PointerTypeSymbol thisPointerType
					&& thisPointerType.ReferencedType is EnumTypeSymbol enumSelf)
				{
					var variant = enumSelf.FindVariant(name);
					if (variant is not null)
					{
						return LLVMValueRef.CreateConstInt(
							codegen.Types.Lower(enumSelf),
							unchecked((ulong)variant.Value));
					}
				}

				var thisType = Function.VariableTypes["this"] as PointerTypeSymbol;
				var structType = thisType?.ReferencedType as StructTypeSymbol;
				var field = structType?.FindField(name);
				if (field is not null)
				{
					var actualThisPointer = Builder.BuildLoad2(
						LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
						thisStorage,
						"loaded_this_ptr");

					var fieldIndex = codegen.AggregateLayout.GetFieldIndex(structType!, name);
					var structLayout = codegen.Types.Lower(structType!);
					var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
					var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);
					var fieldPointer = Builder.BuildGEP2(
						structLayout,
						actualThisPointer,
						new LLVMValueRef[] { zero, index },
						"this_field_ptr");
					var fieldValue = Builder.BuildLoad2(codegen.Types.Lower(field.Type), fieldPointer, "this_field_val");
					aggregates.ApplyTbaa(aggregates.GetTbaaTag(structType!, fieldIndex), fieldValue);
					return fieldValue;
				}
			}

			throw new InvalidOperationException($"Undefined variable '{name}'");
		}

		var type = Function.VariableTypes[name];

		if (Function.HeapAllocatedVars.Contains(name) && type is StructTypeSymbol heapStruct)
		{
			var innerType = codegen.Types.Lower(heapStruct);
			var blockPointer = Builder.BuildLoad2(
				LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
				storage,
				"heap_block_ptr");
			return Builder.BuildLoad2(innerType, blockPointer, "heap_load_val");
		}

		var value = Builder.BuildLoad2(codegen.Types.Lower(type), storage, "load_val");
		if (type is PointerTypeSymbol pointerType)
		{
			var referencedType = pointerType.ReferencedType;
			if (referencedType == TypeSymbol.Int
				|| referencedType == TypeSymbol.Double
				|| referencedType == TypeSymbol.Bool
				|| referencedType == TypeSymbol.Char
				|| referencedType is EnumTypeSymbol)
			{
				return Builder.BuildLoad2(codegen.Types.Lower(referencedType), value, "deref_val");
			}
		}

		return value;
	}
}
