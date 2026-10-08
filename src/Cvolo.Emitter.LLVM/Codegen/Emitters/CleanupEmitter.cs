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

	/// <summary>Returns whether the type carries a recursive destruction obligation.</summary>
	public bool TypeRequiresDestruction(TypeSymbol type) => TypeNeedsDestruction(type);

	private static string SanitizeFlagName(string path)
	{
		Span<char> buffer = stackalloc char[path.Length];
		for (var i = 0; i < path.Length; i++)
		{
			var c = path[i];
			buffer[i] = char.IsLetterOrDigit(c) ? c : '_';
		}
		return new string(buffer);
	}

	/// <summary>
	/// Returns the runtime liveness flag for a move path, creating a function-entry alloca
	/// initialized to false on first use.
	/// </summary>
	public LLVMValueRef GetOrCreateLiveFlag(FunctionCodegenContext function, string path)
	{
		if (function.LiveFlags.TryGetValue(path, out var existing))
		{
			return existing;
		}

		var flag = Builder.BuildAlloca(LLVMTypeRef.Int1, $"live_{SanitizeFlagName(path)}");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0), flag);
		function.LiveFlags[path] = flag;
		return flag;
	}

	/// <summary>Sets the runtime liveness flag for a move path to the given value.</summary>
	public void SetLiveFlag(FunctionCodegenContext function, string path, bool live)
	{
		var flag = GetOrCreateLiveFlag(function, path);
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, live ? 1UL : 0UL), flag);
	}

	/// <summary>
	/// Runs <paramref name="body"/> only when the runtime liveness flag for <paramref name="path"/>
	/// is true. When no flag exists the body runs unconditionally, preserving prior behavior for
	/// projections that never participate in conditional ownership flow.
	/// </summary>
	public void EmitGuardedByLiveFlag(FunctionCodegenContext? function, string path, Action body)
	{
		if (function is null || !function.LiveFlags.TryGetValue(path, out var flag))
		{
			body();
			return;
		}

		var currentFunc = Builder.InsertBlock.Parent;
		var doBlock = currentFunc.AppendBasicBlock($"live_{SanitizeFlagName(path)}_do");
		var afterBlock = currentFunc.AppendBasicBlock($"live_{SanitizeFlagName(path)}_after");
		var isLive = Builder.BuildLoad2(LLVMTypeRef.Int1, flag, "live_flag");
		Builder.BuildCondBr(isLive, doBlock, afterBlock);
		Builder.PositionAtEnd(doBlock);
		body();
		Builder.BuildBr(afterBlock);
		Builder.PositionAtEnd(afterBlock);
	}

	/// <summary>
	/// Returns (creating on first use) the per-element liveness flag array for an owned fixed
	/// array root. Element i is true only while element i holds a live owned value, so the array
	/// destructor loop and element replacement touch only live elements. Missing entries therefore
	/// must never be destroyed.
	/// </summary>
	public LLVMValueRef GetOrCreateElementFlags(FunctionCodegenContext function, string root, int size)
	{
		if (function.ElementLiveFlagArrays.TryGetValue(root, out var existing))
			return existing;

		var flagsType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int1, (uint)size);
		var flags = Builder.BuildAlloca(flagsType, $"live_{SanitizeFlagName(root)}_elems");
		for (var i = 0; i < size; i++)
		{
			var slot = Builder.BuildGEP2(flagsType, flags, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)i)
			}, $"live_elem_init_{i}");
			Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0), slot);
		}

		function.ElementLiveFlagArrays[root] = flags;
		return flags;
	}

	/// <summary>Sets the liveness flag for one fixed-index array element (creating flags if needed).</summary>
	public void SetElementFlag(FunctionCodegenContext function, string root, int size, int index, bool live)
	{
		var flags = GetOrCreateElementFlags(function, root, size);
		var slot = Builder.BuildGEP2(LLVMTypeRef.CreateArray(LLVMTypeRef.Int1, (uint)size), flags, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
		}, $"live_elem_set_{index}");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, live ? 1UL : 0UL), slot);
	}

	/// <summary>
	/// Guards <paramref name="body"/> on a runtime i1 condition, splitting the current block into
	/// a body block and a continuation block. Used for per-element array liveness where the
	/// condition is an indexed load rather than a named flag.
	/// </summary>
	public void EmitGuardedByCondition(LLVMValueRef condition, Action body)
	{
		var currentFunc = Builder.InsertBlock.Parent;
		var doBlock = currentFunc.AppendBasicBlock("elem_live_do");
		var afterBlock = currentFunc.AppendBasicBlock("elem_live_after");
		Builder.BuildCondBr(condition, doBlock, afterBlock);
		Builder.PositionAtEnd(doBlock);
		body();
		Builder.BuildBr(afterBlock);
		Builder.PositionAtEnd(afterBlock);
	}

	/// <summary>
	/// Emits cleanup for the selected variables in a function frame, respecting the frame's
	/// moved/disposed/heap ownership state.
	/// </summary>
	public void EmitScopeCleanup(FunctionCodegenContext function, IEnumerable<string> variableNames, bool skipHeapFree = false)
	{
		foreach (var name in variableNames)
		{
			if (function.DisposedVars.Contains(name))
			{
				continue;
			}

			var isHeap = function.HeapAllocatedVars.Contains(name);
			if (isHeap && skipHeapFree)
			{
				continue;
			}

			// When a runtime liveness flag exists it is the sole authority for this projection:
			// a compile-time "moved" mark would incorrectly suppress cleanup on paths where the
			// move did not execute. Without a flag the previous name-based skip is preserved.
			var hasFlag = function.LiveFlags.ContainsKey(name);
			if (!hasFlag && function.MovedVars.Contains(name))
			{
				continue;
			}

			function.DisposedVars.Add(name);

			var ptrAlloc = function.Locals[name];
			var type = function.VariableTypes[name];

			EmitGuardedByLiveFlag(hasFlag ? function : null, name, () =>
				EmitScopeDestruction(function, name, ptrAlloc, type, isHeap, skipHeapFree));
		}
	}

	private void EmitScopeDestruction(FunctionCodegenContext function, string name, LLVMValueRef ptrAlloc, TypeSymbol type, bool isHeap, bool skipHeapFree)
	{
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
					EmitNestedFieldDestruction(thisPtr, structType, name, name, function);
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
				EmitArrayDestructorLoop(ptrAlloc, arrayType, name, function);
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
			EmitNestedFieldDestruction(enumeratorAlloca, structType, "__fe_enumerator", "__fe_enumerator", null);
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
	public bool UnionNeedsTagCheckedCleanup(UnionTypeSymbol unionType) => UnionNeedsTagCheckedCleanup(unionType, []);

	private bool UnionNeedsTagCheckedCleanup(UnionTypeSymbol unionType, HashSet<string> active)
	{
		if (unionType.IsUnsafe)
			return false;
		if (!active.Add(unionType.Name))
			return false;

		var owned = unionType.Fields.Any(f => !f.IsVoidVariant && TypeNeedsDestruction(f.Type, active));
		active.Remove(unionType.Name);
		return owned;
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
	private void EmitArrayDestructorLoop(LLVMValueRef ptrAlloc, ArrayTypeSymbol arrayType, string name, FunctionCodegenContext? function = null)
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

		// Per-element liveness: a fixed-array element that was never initialized (or whose
		// ownership already moved out) must not be destroyed. When element flags exist the
		// destruction is conditioned on the runtime flag value.
		if (function is not null && function.ElementLiveFlagArrays.TryGetValue(name, out var flagsArray))
		{
			var flagsType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int1, (uint)arrayType.Size);
			var flagPtr = Builder.BuildGEP2(flagsType, flagsArray, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				iVal
			}, $"{name}_arr_flag");
			var elementLive = Builder.BuildLoad2(LLVMTypeRef.Int1, flagPtr, $"{name}_arr_live");
			EmitGuardedByCondition(elementLive, () => EmitElementDestructor(elementPtr, arrayType.ElementType, name));
		}
		else
		{
			EmitElementDestructor(elementPtr, arrayType.ElementType, name);
		}

		var next = Builder.BuildSub(iVal, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1), $"{name}_arr_dec");
		Builder.BuildStore(next, indexAlloca);
		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Destroys one owned value in place at <paramref name="valuePtr"/>, recursively handling
	/// structs, resource-owning unions, and static arrays. Exposed so replacement lowering can
	/// drop a live destination value before installing a replacement.
	/// </summary>
	public void EmitDestructionAt(LLVMValueRef valuePtr, TypeSymbol type, string name)
		=> EmitElementDestructor(valuePtr, type, name);

	/// <summary>
	/// Emits in-place destruction for one value, recursively handling structs, resource-owning
	/// unions, and static arrays.
	/// </summary>
	private void EmitElementDestructor(LLVMValueRef valuePtr, TypeSymbol type, string name, string? path = null, FunctionCodegenContext? function = null)
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
					EmitNestedFieldDestruction(valuePtr, structType, name, path ?? name, function);
				}

				return;

			case UnionTypeSymbol unionType:
				if (UnionNeedsTagCheckedCleanup(unionType))
				{
					EmitUnionTagCheckedCleanup(name, valuePtr, unionType);
				}

				return;

			case ArrayTypeSymbol arrayType:
				EmitArrayDestructorLoop(valuePtr, arrayType, name, function);
				return;
		}
	}

	/// <summary>
	/// Drops every resource-owning field of a struct that has no destructor of its own.
	/// </summary>
	private void EmitNestedFieldDestruction(LLVMValueRef valuePtr, StructTypeSymbol structType, string name, string path, FunctionCodegenContext? function)
	{
		var structLayout = codegen.Types.Lower(structType);
		foreach (var field in structType.Fields)
		{
			if (!TypeNeedsDestruction(field.Type))
			{
				continue;
			}

			var childPath = $"{path}.{field.Name}";
			var hasFlag = function is not null && function.LiveFlags.ContainsKey(childPath);
			if (!hasFlag && function is not null && function.MovedFieldPaths.Contains(childPath))
			{
				continue;
			}

			var fieldPtr = Builder.BuildGEP2(structLayout, valuePtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)codegen.AggregateLayout.GetFieldIndex(structType, field.Name))
			}, $"{name}_f_{field.Name}");
			EmitGuardedByLiveFlag(hasFlag ? function : null, childPath, () =>
				EmitElementDestructor(fieldPtr, field.Type, name, childPath, function));
		}
	}

	/// <summary>
	/// Returns whether a semantic type carries a recursive destruction obligation.
	/// Aggregate graphs are cycle-safe: revisiting an aggregate on the active path cuts that branch.
	/// </summary>
	private bool TypeNeedsDestruction(TypeSymbol type) => TypeNeedsDestruction(type, []);

	private bool TypeNeedsDestruction(TypeSymbol type, HashSet<string> active)
	{
		switch (type)
		{
			case StructTypeSymbol structType:
				if (BindingContext.OverloadedFunctions.ContainsKey($"{structType.Name}.~{structType.Name}"))
					return true;
				if (!active.Add(structType.Name))
					return false;
				var structOwned = structType.Fields.Any(f => TypeNeedsDestruction(f.Type, active));
				active.Remove(structType.Name);
				return structOwned;
			case UnionTypeSymbol unionType:
				return UnionNeedsTagCheckedCleanup(unionType, active);
			case ArrayTypeSymbol arrayType:
				return TypeNeedsDestruction(arrayType.ElementType, active);
			default:
				return false;
		}
	}

}