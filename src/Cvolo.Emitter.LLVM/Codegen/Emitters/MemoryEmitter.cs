using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits low-level LLVM memory operations shared by higher-level code generation components.
/// </summary>
/// <remarks>
/// This emitter intentionally contains only storage mechanics. It does not decide Cvolo expression
/// semantics, aggregate initialization rules, ownership, or native ABI classification. Higher-level
/// emitters choose what must be allocated or cleared and delegate the corresponding LLVM operations
/// here.
/// </remarks>
/// <remarks>
/// Creates a memory emitter backed by the module-lifetime LLVM context.
/// </remarks>
/// <param name="codegen">Shared LLVM and module state for the current emission run.</param>
internal sealed class MemoryEmitter(CodegenContext codegen)
{
	private LLVMBuilderRef Builder => codegen.Builder;

	/// <summary>
	/// Allocates unmanaged storage large enough for one value of the supplied LLVM type using the
	/// existing runtime <c>malloc</c> declaration.
	/// </summary>
	/// <param name="type">LLVM type whose concrete store size determines the allocation size.</param>
	/// <param name="name">IR name assigned to the emitted allocation call.</param>
	/// <returns>The raw pointer returned by <c>malloc</c>.</returns>
	public LLVMValueRef AllocateHeap(LLVMTypeRef type, string name = "heap_alloc")
	{
		var byteCount = LLVMValueRef.CreateConstInt(
			LLVMTypeRef.Int64,
			(ulong)Math.Max(1, GetStoreSize(type)));

		var mallocFunc = codegen.Globals["malloc"];
		var mallocType = codegen.FunctionTypes["malloc"];
		return Builder.BuildCall2(mallocType, mallocFunc, new LLVMValueRef[] { byteCount }, name);
	}

	/// <summary>
	/// Allocates unmanaged storage for a runtime number of elements while preserving the existing
	/// LLVM GEP-based element-size calculation.
	/// </summary>
	/// <param name="elementType">LLVM element type stored in the allocation.</param>
	/// <param name="elementCount">Runtime element count produced by expression emission.</param>
	/// <param name="name">IR name assigned to the emitted allocation call.</param>
	/// <returns>The raw pointer returned by <c>malloc</c>.</returns>
	public LLVMValueRef AllocateHeapArray(LLVMTypeRef elementType, LLVMValueRef elementCount, string name = "heap_arr_alloc")
	{
		var count64 = Builder.BuildZExt(elementCount, LLVMTypeRef.Int64, "count_64");

		var nullPtr = LLVMValueRef.CreateConstPointerNull(LLVMTypeRef.CreatePointer(elementType, 0));
		var sizePtr = Builder.BuildGEP2(
			elementType,
			nullPtr,
			new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) },
			"size_ptr");
		var elementSize64 = Builder.BuildPtrToInt(sizePtr, LLVMTypeRef.Int64, "element_size");
		var totalSize = Builder.BuildMul(count64, elementSize64, "total_alloc_size");

		var mallocFunc = codegen.Globals["malloc"];
		var mallocType = codegen.FunctionTypes["malloc"];
		return Builder.BuildCall2(mallocType, mallocFunc, new LLVMValueRef[] { totalSize }, name);
	}

	/// <summary>
	/// Emits an alloca in the entry block of the current function, then restores the builder's
	/// original insertion point.
	/// </summary>
	/// <param name="type">LLVM type of the stack slot.</param>
	/// <param name="name">IR name assigned to the alloca.</param>
	/// <returns>The pointer produced by the entry-block alloca.</returns>
	public LLVMValueRef BuildEntryAlloca(LLVMTypeRef type, string name)
	{
		var currentBlock = Builder.InsertBlock;
		var currentFunc = currentBlock.Parent;
		var entryBlock = currentFunc.EntryBasicBlock;

		if (entryBlock.FirstInstruction.Handle != IntPtr.Zero)
			Builder.PositionBefore(entryBlock.FirstInstruction);
		else
			Builder.PositionAtEnd(entryBlock);

		var alloca = Builder.BuildAlloca(type, name);
		Builder.PositionAtEnd(currentBlock);
		return alloca;
	}

	/// <summary>
	/// Clears a contiguous storage region through the module's existing runtime <c>memset</c>
	/// declaration.
	/// </summary>
	/// <param name="destination">Pointer to the first byte of the storage region.</param>
	/// <param name="byteCount">Number of bytes to clear; non-positive sizes emit no call.</param>
	public void ZeroMemory(LLVMValueRef destination, long byteCount)
	{
		if (byteCount <= 0)
			return;

		var memsetFunc = codegen.Globals["memset"];
		var memsetType = codegen.FunctionTypes["memset"];
		var destinationBytes = Builder.BuildBitCast(
			destination,
			LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
			"zero_dest");
		Builder.BuildCall2(memsetType, memsetFunc, new LLVMValueRef[]
		{
			destinationBytes,
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)byteCount)
		}, "");
	}

	/// <summary>
	/// Returns the target-data store size of an LLVM type, including alignment padding encoded in
	/// the module data layout.
	/// </summary>
	/// <param name="type">LLVM type whose concrete storage size is required.</param>
	/// <returns>The store size in bytes for the active module data layout.</returns>
	public long GetStoreSize(LLVMTypeRef type)
	{
		var targetData = LLVMTargetDataRef.FromStringRepresentation(codegen.Module.DataLayout);
		return (long)targetData.StoreSizeOfType(type);
	}
}
