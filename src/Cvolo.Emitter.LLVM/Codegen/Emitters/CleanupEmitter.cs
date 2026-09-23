using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits destruction and ownership cleanup for values whose lifetime ends during LLVM code
/// generation.
/// </summary>
/// <remarks>
/// This emitter owns cleanup IR only. It does not decide lexical lifetime, move validity, or
/// ownership-transfer semantics; those decisions remain represented by <see cref="FunctionCodegenContext"/>
/// and the already-bound semantic model. The implementation intentionally preserves the cleanup
/// behavior that previously lived in <see cref="CodeGenerator"/>.
/// </remarks>
/// <remarks>
/// Creates a cleanup emitter backed by the module-lifetime code generation context.
/// </remarks>
internal sealed class CleanupEmitter(CodegenContext codegen)
{
	private LLVMBuilderRef Builder => codegen.Builder;
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Cleanup emission requires an active binding context.");

	/// <summary>
	/// Emits cleanup for the selected variables in a function frame, respecting the frame's
	/// moved/disposed/heap ownership state.
	/// </summary>
	public void EmitScopeCleanup(FunctionCodegenContext function, IEnumerable<string> variableNames, bool skipHeapFree = false)
	{
		foreach (var name in variableNames)
		{
			if (function.MovedVars.Contains(name) || function.DisposedVars.Contains(name))
			{
				continue;
			}

			var isHeap = function.HeapAllocatedVars.Contains(name);
			if (isHeap && skipHeapFree)
			{
				continue;
			}

			function.DisposedVars.Add(name);

			var ptrAlloc = function.Locals[name];
			var type = function.VariableTypes[name];

			// Call the type's destructor only for owned structs. A struct without its own
			// destructor still drops resource-owning fields transitively.
			if (type is StructTypeSymbol structType)
			{
				var disposeBaseName = $"{structType.Name}.~{structType.Name}";

				LLVMValueRef thisPtr;
				if (function.HeapAllocatedVars.Contains(name))
				{
					thisPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptrAlloc, "this_ptr");
				}
				else
				{
					thisPtr = ptrAlloc;
				}

				if (BindingContext.OverloadedFunctions.TryGetValue(disposeBaseName, out var candidates) && candidates.Count > 0)
				{
					var disposeSymbol = candidates[0];
					var callee = codegen.Globals[disposeSymbol.Name];
					var funcType = codegen.FunctionTypes[disposeSymbol.Name];

					Builder.BuildCall2(funcType, callee, new LLVMValueRef[] { thisPtr }, "");
				}
				else
				{
					EmitNestedFieldDestruction(thisPtr, structType, name);
				}
			}

			// Resource-owning unions require tag-checked destruction of the active payload.
			if (type is UnionTypeSymbol unionType)
			{
				EmitUnionTagCheckedCleanup(name, ptrAlloc, unionType);
			}

			// Static arrays destroy resource-owning elements in reverse index order.
			if (type is ArrayTypeSymbol arrayType)
			{
				EmitArrayDestructorLoop(ptrAlloc, arrayType, name);
			}

			// Heap storage is released after value destruction unless the caller requested the
			// existing ownership-transfer cleanup behavior.
			if (function.HeapAllocatedVars.Contains(name) && !skipHeapFree)
			{
				LLVMValueRef actualHeapPtr;
				if (type is SliceTypeSymbol sliceType)
				{
					var sliceLayout = codegen.Types.Lower(sliceType);
					var ptrField = Builder.BuildGEP2(sliceLayout, ptrAlloc, new LLVMValueRef[] {
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
					}, "slice_ptr_field");
					actualHeapPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptrField, "heap_ptr");
				}
				else
				{
					actualHeapPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptrAlloc, "heap_ptr");
				}

				var freeFunc = codegen.Globals["free"];
				var freeType = codegen.FunctionTypes["free"];
				Builder.BuildCall2(freeType, freeFunc, new LLVMValueRef[] { actualHeapPtr }, "");
			}
		}
	}

	/// <summary>
	/// Emits deferred destruction of the hidden foreach enumerator after the loop completes.
	/// </summary>
	public void EmitForEachEnumeratorCleanup(LLVMValueRef enumeratorAlloca, TypeSymbol enumeratorType)
	{
		if (enumeratorType is not StructTypeSymbol structType)
		{
			return;
		}

		var disposeBaseName = $"{structType.Name}.~{structType.Name}";
		if (BindingContext.OverloadedFunctions.TryGetValue(disposeBaseName, out var candidates) && candidates.Count > 0)
		{
			var disposeSymbol = candidates[0];
			var callee = codegen.Globals[disposeSymbol.Name];
			var funcType = codegen.FunctionTypes[disposeSymbol.Name];
			Builder.BuildCall2(funcType, callee, new LLVMValueRef[] { enumeratorAlloca }, "");
		}
		else
		{
			EmitNestedFieldDestruction(enumeratorAlloca, structType, "__fe_enumerator");
		}
	}


	/// <summary>
	/// Returns whether a type can transitively expose heap-owned storage through a reference-bearing
	/// field, variant payload, array element, or slice element.
	/// </summary>
	/// <remarks>
	/// The result is used by existing ownership-transfer decisions. It intentionally preserves the
	/// previous recursive classification without introducing new escape-analysis semantics.
	/// </remarks>
	public bool TypeEscapesHeap(TypeSymbol type)
	{
		return type switch
		{
			PointerTypeSymbol or RawPointerTypeSymbol => true,
			StructTypeSymbol structType => structType.Fields.Any(field => TypeEscapesHeap(field.Type)),
			UnionTypeSymbol { IsUnsafe: true } => false,
			UnionTypeSymbol unionType => unionType.Fields.Any(field => TypeEscapesHeap(field.Type)),
			ArrayTypeSymbol arrayType => TypeEscapesHeap(arrayType.ElementType),
			SliceTypeSymbol sliceType => TypeEscapesHeap(sliceType.ElementType),
			_ => false,
		};
	}

	/// <summary>
	/// Returns whether a union can hold a payload that carries a destruction obligation.
	/// </summary>
	public bool UnionNeedsTagCheckedCleanup(UnionTypeSymbol unionType)
	{
		return !unionType.IsUnsafe && unionType.Fields.Any(f => !f.IsVoidVariant && TypeNeedsDestruction(f.Type));
	}

	/// <summary>
	/// Emits tag-checked destruction for a resource-owning union. The active tag is reset to the
	/// None variant before the payload destructor runs so destructor failure cannot drop the same
	/// payload twice.
	/// </summary>
	public void EmitUnionTagCheckedCleanup(string name, LLVMValueRef ptrAlloc, UnionTypeSymbol unionType)
	{
		if (unionType.IsUnsafe)
			return;

		var dropped = unionType.Fields
			.Where(f => !f.IsVoidVariant)
			.Select(f => (Field: f, Index: codegen.AggregateLayout.GetFieldIndex(unionType, f!.Name)))
			.Where(t => TypeNeedsDestruction(t.Field.Type))
			.ToList();

		if (dropped.Count == 0)
		{
			return;
		}

		var unionLayout = codegen.Types.Lower(unionType);
		var currentFunc = Builder.InsertBlock.Parent;

		var tagPtr = Builder.BuildGEP2(unionLayout, ptrAlloc, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
		}, "union_tag_ptr");
		var tagVal = Builder.BuildLoad2(LLVMTypeRef.Int8, tagPtr, "union_tag_val");

		var noneIndex = unionType.NoneVariant is not null ? codegen.AggregateLayout.GetFieldIndex(unionType, unionType.NoneVariant.Name) : 0;
		var noneTag = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)noneIndex);
		var after = currentFunc.AppendBasicBlock($"{name}_cleanup_after");

		for (var i = 0; i < dropped.Count; i++)
		{
			var (field, fieldIndex) = dropped[i];
			var isLast = i == dropped.Count - 1;

			var failBlock = isLast ? after : currentFunc.AppendBasicBlock($"{name}_cleanup_chk_{i + 1}");
			var dropBlock = currentFunc.AppendBasicBlock($"{name}_cleanup_drop_{i}");

			var isMatch = Builder.BuildICmp(
				LLVMIntPredicate.LLVMIntEQ,
				tagVal,
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex),
				"tag_match");
			Builder.BuildCondBr(isMatch, dropBlock, failBlock);

			Builder.PositionAtEnd(dropBlock);
			Builder.BuildStore(noneTag, tagPtr);
			var payloadPtr = Builder.BuildGEP2(unionLayout, ptrAlloc, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
			}, $"{name}_payload");
			var castPtr = Builder.BuildBitCast(
				payloadPtr,
				LLVMTypeRef.CreatePointer(codegen.Types.Lower(field.Type), 0),
				$"{name}_payload_ptr");
			EmitElementDestructor(castPtr, field.Type, $"{name}_u");
			Builder.BuildBr(after);

			Builder.PositionAtEnd(failBlock);
		}

		Builder.PositionAtEnd(after);
	}

	/// <summary>
	/// Emits the reverse-index destructor loop for a static array whose element type requires
	/// destruction.
	/// </summary>
	private void EmitArrayDestructorLoop(LLVMValueRef ptrAlloc, ArrayTypeSymbol arrayType, string name)
	{
		if (!TypeNeedsDestruction(arrayType.ElementType))
			return;

		var currentFunc = Builder.InsertBlock.Parent;
		var arrayLayout = codegen.Types.Lower(arrayType);

		var indexAlloca = Builder.BuildAlloca(LLVMTypeRef.Int32, $"{name}_arr_i");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)Math.Max(0, arrayType.Size - 1)), indexAlloca);

		var condBlock = currentFunc.AppendBasicBlock($"{name}_arr_cond");
		var bodyBlock = currentFunc.AppendBasicBlock($"{name}_arr_body");
		var endBlock = currentFunc.AppendBasicBlock($"{name}_arr_end");

		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(condBlock);
		var iVal = Builder.BuildLoad2(LLVMTypeRef.Int32, indexAlloca, $"{name}_arr_i_val");
		var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
		var cond = Builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, iVal, zero, $"{name}_arr_cond");
		Builder.BuildCondBr(cond, bodyBlock, endBlock);

		Builder.PositionAtEnd(bodyBlock);
		var elementPtr = Builder.BuildGEP2(arrayLayout, ptrAlloc, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			iVal
		}, $"{name}_arr_elem");
		EmitElementDestructor(elementPtr, arrayType.ElementType, name);
		var next = Builder.BuildSub(iVal, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1), $"{name}_arr_dec");
		Builder.BuildStore(next, indexAlloca);
		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Emits in-place destruction for one value, recursively handling structs, resource-owning
	/// unions, and static arrays.
	/// </summary>
	private void EmitElementDestructor(LLVMValueRef valuePtr, TypeSymbol type, string name)
	{
		switch (type)
		{
			case StructTypeSymbol structType:
				var disposeBase = $"{structType.Name}.~{structType.Name}";
				if (BindingContext.OverloadedFunctions.TryGetValue(disposeBase, out var disposeSymbols))
				{
					var disposeSymbol = disposeSymbols.First();
					var disposeCallee = codegen.Globals[disposeSymbol.Name];
					var disposeType = codegen.FunctionTypes[disposeSymbol.Name];
					Builder.BuildCall2(disposeType, disposeCallee, new LLVMValueRef[] { valuePtr }, "");
				}
				else
				{
					EmitNestedFieldDestruction(valuePtr, structType, name);
				}

				return;

			case UnionTypeSymbol unionType:
				if (UnionNeedsTagCheckedCleanup(unionType))
				{
					EmitUnionTagCheckedCleanup(name, valuePtr, unionType);
				}

				return;

			case ArrayTypeSymbol arrayType:
				EmitArrayDestructorLoop(valuePtr, arrayType, name);
				return;
		}
	}

	/// <summary>
	/// Drops every resource-owning field of a struct that has no destructor of its own.
	/// </summary>
	private void EmitNestedFieldDestruction(LLVMValueRef valuePtr, StructTypeSymbol structType, string name)
	{
		var structLayout = codegen.Types.Lower(structType);
		foreach (var field in structType.Fields)
		{
			if (!TypeNeedsDestruction(field.Type))
			{
				continue;
			}

			var fieldPtr = Builder.BuildGEP2(structLayout, valuePtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)codegen.AggregateLayout.GetFieldIndex(structType, field.Name))
			}, $"{name}_f_{field.Name}");
			EmitElementDestructor(fieldPtr, field.Type, name);
		}
	}

	/// <summary>
	/// Returns whether a semantic type carries a recursive destruction obligation.
	/// </summary>
	private bool TypeNeedsDestruction(TypeSymbol type) => type switch
	{
		StructTypeSymbol structType =>
			BindingContext.OverloadedFunctions.ContainsKey($"{structType.Name}.~{structType.Name}")
			|| structType.Fields.Any(f => TypeNeedsDestruction(f.Type)),
		UnionTypeSymbol unionType => UnionNeedsTagCheckedCleanup(unionType),
		ArrayTypeSymbol arrayType => TypeNeedsDestruction(arrayType.ElementType),
		_ => false
	};
}
