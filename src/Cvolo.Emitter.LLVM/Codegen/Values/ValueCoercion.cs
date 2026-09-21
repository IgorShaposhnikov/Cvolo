using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Values;

/// <summary>
/// Applies representation-preserving LLVM value coercions required by Cvolo code generation.
/// </summary>
/// <remarks>
/// This class centralizes the existing integer-width, floating-point-width, reference-to-value,
/// and array-to-slice conversions. It deliberately does not decide whether a language-level
/// conversion is legal; semantic validation and binding make that decision before codegen.
/// Native ABI classification is also outside this layer.
/// </remarks>
/// <remarks>
/// Creates a coercion helper backed by the active module-level code generation context.
/// </remarks>
internal sealed class ValueCoercion(CodegenContext codegen)
{
	private LLVMBuilderRef Builder => codegen.Builder;

	/// <summary>
	/// Extends or truncates an integer value so its LLVM width matches the target semantic type.
	/// </summary>
	/// <remarks>
	/// Widening preserves the source type's signedness, matching the previous in-place codegen
	/// behavior. Values whose source or target is not an integer are returned unchanged.
	/// </remarks>
	public LLVMValueRef CoerceIntegerWidth(LLVMValueRef value, TypeSymbol fromType, TypeSymbol toType)
	{
		if (!TypeSymbol.IsIntegerType(fromType) || !TypeSymbol.IsIntegerType(toType))
			return value;

		var fromWidth = TypeSymbol.IntegerBitWidth(fromType) is var fw ? fw : 0;
		var toWidth = TypeSymbol.IntegerBitWidth(toType) is var tw ? tw : 0;

		if (fromWidth == toWidth)
			return value;

		var llvmTarget = codegen.Types.Lower(toType);

		if (fromWidth > toWidth)
			return Builder.BuildTrunc(value, llvmTarget, "store_trunc");

		return TypeSymbol.IsSignedIntegerType(fromType)
			? Builder.BuildSExt(value, llvmTarget, "store_sext")
			: Builder.BuildZExt(value, llvmTarget, "store_zext");
	}

	/// <summary>
	/// Extends or truncates a floating-point value to the target floating-point width.
	/// </summary>
	/// <remarks>
	/// Non-floating-point values and values already matching the target semantic type are returned
	/// unchanged. This preserves the existing float/double store coercion behavior.
	/// </remarks>
	public LLVMValueRef CoerceFloatWidth(LLVMValueRef value, TypeSymbol fromType, TypeSymbol toType)
	{
		if (!TypeSymbol.IsFloatingPointType(fromType) || !TypeSymbol.IsFloatingPointType(toType))
			return value;

		if (fromType.Equals(toType))
			return value;

		return toType.Equals(TypeSymbol.Double)
			? Builder.BuildFPExt(value, LLVMTypeRef.Double, "store_fpext")
			: Builder.BuildFPTrunc(value, LLVMTypeRef.Float, "store_fptrunc");
	}

	/// <summary>
	/// Dereferences a <c>ref T</c>-represented pointer when a value destination consumes the
	/// referenced <c>T</c> rather than the reference itself.
	/// </summary>
	/// <remarks>
	/// Reference-preserving paths must bypass this helper. In particular, ref-variable declarations
	/// and switch promotion continue to consume the pointer directly.
	/// </remarks>
	public LLVMValueRef CoerceReferenceToValue(LLVMValueRef value, TypeSymbol expressionType)
	{
		if (expressionType is PointerTypeSymbol ptrType && ptrType.ReferencedType is not null)
		{
			return Builder.BuildLoad2(codegen.Types.Lower(ptrType.ReferencedType), value, "ref_to_value");
		}

		return value;
	}

	/// <summary>
	/// Materializes the existing two-field slice representation from an array storage pointer.
	/// </summary>
	/// <remarks>
	/// The emitted slice contains a byte pointer to the array storage and the statically known array
	/// element count. This method intentionally preserves the current temporary-allocation lowering.
	/// </remarks>
	public LLVMValueRef CoerceArrayToSlice(LLVMValueRef arrayPtr, TypeSymbol arrayType, SliceTypeSymbol sliceType)
	{
		var resolvedArrayType = arrayType is PointerTypeSymbol ptr
			? ptr.ReferencedType as ArrayTypeSymbol
			: arrayType as ArrayTypeSymbol;

		var fatStructType = codegen.Types.Lower(sliceType);
		var sliceAlloc = Builder.BuildAlloca(fatStructType, "slice_tmp");

		var ptrField = Builder.BuildGEP2(
			fatStructType,
			sliceAlloc,
			new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			},
			"ptr_field");
		var castPtr = Builder.BuildBitCast(arrayPtr, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
		Builder.BuildStore(castPtr, ptrField);

		var sizeField = Builder.BuildGEP2(
			fatStructType,
			sliceAlloc,
			new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
			},
			"size_field");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)resolvedArrayType!.Size), sizeField);

		return Builder.BuildLoad2(fatStructType, sliceAlloc, "slice_val");
	}

	/// <summary>
	/// Preserves an already-compatible value or emits the legacy LLVM bitcast used when codegen needs
	/// to reinterpret a value as another representation-compatible LLVM type.
	/// </summary>
	/// <remarks>
	/// Pointer-to-pointer conversions are returned unchanged because LLVM's opaque-pointer model does
	/// not require an instruction for those conversions. This exactly preserves the former
	/// <c>CodeGenerator.SafeBitCast</c> behavior.
	/// </remarks>
	public LLVMValueRef SafeBitCast(LLVMValueRef value, LLVMTypeRef targetType, string name = "")
	{
		if (value.TypeOf.Handle == targetType.Handle)
			return value;

		if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind
			&& targetType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
		{
			return value;
		}

		return Builder.BuildBitCast(value, targetType, name);
	}

}
