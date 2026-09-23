using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.TypeLowering;

/// <summary>
/// Converts Cvolo semantic types into the LLVM types used by the compiler's internal IR.
/// </summary>
/// <remarks>
/// This class intentionally models only the compiler's internal representation. Native ABI
/// classification, parameter coercion, return classification, and platform calling-convention
/// rules belong to a separate native-ABI layer.
///
/// The implementation preserves the pre-refactoring behavior exactly. In particular, the
/// <c>bool -&gt; i1</c> choice is retained; native-sized integers (<c>nint</c>/<c>nuint</c>) are
/// lowered to the target pointer width supplied at construction time.
/// </remarks>
internal sealed class LlvmTypeLowering(IReadOnlyDictionary<string, LLVMTypeRef> llvmStructTypes, int nativePointerBits)
{
	/// <summary>
	/// Named LLVM aggregate types that were declared for semantic structs and unions.
	/// The dictionary is shared with module-level code generation and is populated as declarations
	/// are materialized.
	/// </summary>
	private readonly IReadOnlyDictionary<string, LLVMTypeRef> _llvmStructTypes = llvmStructTypes;

	private readonly int _nativePointerBits = nativePointerBits;

	/// <summary>
	/// Cached safe-delegate storage type. Safe delegates currently use two pointer-sized words:
	/// one invoke pointer and one context pointer.
	/// </summary>
	private LLVMTypeRef _delegateWordType = default;

	/// <summary>
	/// Lowers a semantic type to its current internal LLVM representation.
	/// </summary>
	/// <param name="t">Semantic type produced by analysis.</param>
	/// <returns>The LLVM type used when emitting normal Cvolo IR.</returns>
	public LLVMTypeRef Lower(TypeSymbol t)
	{
		// Preserve the previous defensive fallback used by CodeGenerator.GetLLVMType.
		if (t is null)
			return LLVMTypeRef.Int32;

		// Managed/safe pointer values are represented as opaque byte pointers internally.
		if (t is PointerTypeSymbol)
			return LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

		// Raw pointers preserve their element type in the current LLVM representation.
		if (t is RawPointerTypeSymbol rawPtr)
			return LLVMTypeRef.CreatePointer(Lower(rawPtr.ElementType), 0);

		// Slices are represented as { data pointer, 32-bit length }.
		if (t is SliceTypeSymbol)
		{
			var opaquePtr = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
			return LLVMTypeRef.CreateStruct(new LLVMTypeRef[] { opaquePtr, LLVMTypeRef.Int32 }, false);
		}

		if (t is ArrayTypeSymbol arr)
			return LLVMTypeRef.CreateArray(Lower(arr.ElementType), (uint)arr.Size);

		// NPO-eligible unions use the nullable-pointer representation selected by semantic analysis.
		if (t is UnionTypeSymbol union && union.IsNpoEligible)
			return LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

		// Enums use the same LLVM storage type as their declared backing type.
		if (t is EnumTypeSymbol enumType)
			return Lower(enumType.StorageType);

		// Preserve the existing safe-delegate representation exactly during extraction.
		if (t is DelegateTypeSymbol delegateType)
		{
			// Native delegates carry a single function pointer: null==0, one direct call target.
			if (delegateType.IsNative)
			{
				var paramTypes = delegateType.Parameters.Select(p => Lower(p.Type)).ToArray();
				return LLVMTypeRef.CreatePointer(LLVMTypeRef.CreateFunction(Lower(delegateType.ReturnType), paramTypes), 0);
			}

			if (_delegateWordType.Handle == 0)
				_delegateWordType = LLVMTypeRef.CreateStruct(
					[LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false);
			return _delegateWordType;
		}

		// Declared aggregates are resolved through the module's named-type registry.
		if (t is StructTypeSymbol || t is UnionTypeSymbol)
		{
			if (_llvmStructTypes.TryGetValue(t.Name, out var typeRef))
				return typeRef;
		}

		// Keep the original primitive mapping and unknown-type fallback unchanged. Native-sized
		// integers follow the target pointer width resolved from the module data layout.
		return t.Name switch
		{
			"void" => LLVMTypeRef.Void,
			"int" or "uint" => LLVMTypeRef.Int32,
			"long" or "ulong" => LLVMTypeRef.Int64,
			"nint" or "nuint" => LLVMContextRef.Global.GetIntType((uint)_nativePointerBits),
			"short" or "ushort" => LLVMTypeRef.Int16,
			"byte" or "sbyte" or "char" => LLVMTypeRef.Int8,
			"float" => LLVMTypeRef.Float,
			"double" => LLVMTypeRef.Double,
			"bool" => LLVMTypeRef.Int1,
			"string" or "ptr" => LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
			_ => _llvmStructTypes.TryGetValue(t.Name, out var foundType) ? foundType : LLVMTypeRef.Int32
		};
	}
}
