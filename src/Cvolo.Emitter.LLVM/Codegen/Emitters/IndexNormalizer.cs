using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Normalizes an already bounds-checked integer index to the <c>i32</c> GEP index expected by the
/// per-element liveness metadata and fixed-array payload lowering. Bounds comparisons must be
/// emitted in the index's original LLVM width so wide values cannot wrap to a valid small slot;
/// only values proven in range are narrowed here.
/// </summary>
internal static class IndexNormalizer
{
	public static LLVMValueRef ToI32AfterBoundsCheck(LLVMBuilderRef builder, LLVMValueRef index)
	{
		var width = index.TypeOf.IntWidth;
		if (width == 32)
			return index;

		return width < 32
			? builder.BuildZExt(index, LLVMTypeRef.Int32, "index_i32")
			: builder.BuildTrunc(index, LLVMTypeRef.Int32, "index_i32");
	}
}
