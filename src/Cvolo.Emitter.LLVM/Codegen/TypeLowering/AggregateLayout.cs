using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Emitter.LLVM.Codegen.TypeLowering;

/// <summary>
/// Provides shared aggregate layout helpers used by LLVM emission without changing the compiler's
/// existing layout semantics.
/// </summary>
/// <remarks>
/// This class centralizes field/variant indexing plus target-pointer-aware natural object layout.
/// It is used for sizeof/raw-union storage decisions only; function argument/return ABI
/// classification remains a separate target-specific concern.
/// </remarks>
internal sealed class AggregateLayout(int nativePointerBits = 64)
{
	private readonly int _nativePointerBytes = Math.Max(1, nativePointerBits / 8);

	/// <summary>
	/// Returns the declaration-order index of a field in a Cvolo struct.
	/// </summary>
	/// <exception cref="KeyNotFoundException">Thrown when the requested field is not declared.</exception>
	public int GetFieldIndex(StructTypeSymbol type, string name)
	{
		for (var i = 0; i < type.Fields.Count; i++)
		{
			if (type.Fields[i].Name == name)
				return i;
		}

		throw new KeyNotFoundException($"Field {name} not found in struct {type.Name}");
	}

	/// <summary>
	/// Returns the declaration-order index of a variant in a Cvolo union.
	/// </summary>
	/// <exception cref="KeyNotFoundException">Thrown when the requested variant is not declared.</exception>
	public int GetFieldIndex(UnionTypeSymbol type, string name)
	{
		for (var i = 0; i < type.Fields.Count; i++)
		{
			if (type.Fields[i].Name == name)
				return i;
		}

		throw new KeyNotFoundException($"Variant {name} not found in union {type.Name}");
	}

	/// <summary>
	/// Returns the compiler's current byte-size estimate for a semantic Cvolo type.
	/// </summary>
	/// <remarks>
	/// Structs and raw unions use natural padding/alignment and pointer-sized forms follow the
	/// active target width. Function-call ABI classification is intentionally not inferred here.
	/// </remarks>
	public int GetByteSize(TypeSymbol type)
	{
		if (type is null) return 0;
		if (type.Equals(TypeSymbol.String) || type is PointerTypeSymbol or RawPointerTypeSymbol) return _nativePointerBytes;
		if (type is SliceTypeSymbol) return AlignUp(_nativePointerBytes + 4, Math.Max(_nativePointerBytes, 4));
		if (type is DelegateTypeSymbol delegateType)
			return delegateType.IsNative
				? _nativePointerBytes
				: DelegateTypeSymbol.SafeDelegateWordCount * _nativePointerBytes;
		if (type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt) || type.Equals(TypeSymbol.Float)) return 4;
		if (type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong) || type.Equals(TypeSymbol.Double)) return 8;
		if (type.Equals(TypeSymbol.NInt) || type.Equals(TypeSymbol.NUInt)) return _nativePointerBytes;
		if (type.Equals(TypeSymbol.Short) || type.Equals(TypeSymbol.UShort)) return 2;
		if (type.Equals(TypeSymbol.SByte) || type.Equals(TypeSymbol.Byte) || type.Equals(TypeSymbol.Bool) || type.Equals(TypeSymbol.Char)) return 1;
		if (type is ArrayTypeSymbol arrayType) return GetByteSize(arrayType.ElementType) * arrayType.Size;

		if (type is StructTypeSymbol structType)
			return GetNativeAggregateLayout(structType).Size;

		if (type is UnionTypeSymbol unionType)
		{
			if (unionType.IsNpoEligible)
				return _nativePointerBytes;

			if (unionType.IsUnsafe)
				return GetRawUnionLayout(unionType).ByteSize;

			var maxPayload = unionType.Fields
				.Where(field => !field.IsVoidVariant)
				.Select(field => GetByteSize(field.Type))
				.DefaultIfEmpty(0)
				.Max();
			return 1 + maxPayload;
		}

		if (type is EnumTypeSymbol enumType)
			return GetByteSize(enumType.StorageType);

		return 4;
	}

	/// <summary>
	/// Returns the C-ABI layout of a raw 'unsafe union': its store size is the largest field size
	/// rounded up to the largest field alignment, and its alignment is that largest field alignment.
	/// </summary>
	/// <remarks>
	/// Uses the C natural-alignment rules (each field starts at offset 0; the union's size is
	/// rounded up to its alignment). Arrays and nested structs/raw unions are computed recursively
	/// with C padding so the backing type matches what a C compiler would emit for the union.
	/// </remarks>
	public (int ByteSize, int Alignment) GetRawUnionLayout(UnionTypeSymbol type)
	{
		var maxFieldSize = 0;
		var maxFieldAlignment = 1;
		foreach (var field in type.Fields)
		{
			if (field.IsVoidVariant)
				continue;

			var (size, alignment) = GetNativeAggregateLayout(field.Type);
			maxFieldSize = Math.Max(maxFieldSize, size);
			maxFieldAlignment = Math.Max(maxFieldAlignment, alignment);
		}

		var roundedSize = AlignUp(maxFieldSize, maxFieldAlignment);
		return (roundedSize, maxFieldAlignment);
	}

	public (int Size, int Alignment) GetNativeLayout(TypeSymbol type) => GetNativeAggregateLayout(type);

	private (int Size, int Alignment) GetNativeAggregateLayout(TypeSymbol type)
	{
		if (type.Equals(TypeSymbol.String) || type is PointerTypeSymbol or RawPointerTypeSymbol)
		{
			return (_nativePointerBytes, _nativePointerBytes);
		}

		if (type is SliceTypeSymbol)
		{
			var alignment = Math.Max(_nativePointerBytes, 4);
			return (AlignUp(_nativePointerBytes + 4, alignment), alignment);
		}

		if (type is DelegateTypeSymbol delegateType)
		{
			var words = delegateType.IsNative ? 1 : DelegateTypeSymbol.SafeDelegateWordCount;
			return (words * _nativePointerBytes, _nativePointerBytes);
		}

		if (type is EnumTypeSymbol enumType)
		{
			return GetNativeAggregateLayout(enumType.StorageType);
		}

		if (type is ArrayTypeSymbol arrayType)
		{
			var (elementSize, elementAlignment) = GetNativeAggregateLayout(arrayType.ElementType);
			return (elementSize * arrayType.Size, elementAlignment);
		}

		if (type is StructTypeSymbol structType)
		{
			var offset = 0;
			var alignment = 1;
			foreach (var field in structType.Fields)
			{
				var (fieldSize, fieldAlignment) = GetNativeAggregateLayout(field.Type);
				offset = AlignUp(offset, fieldAlignment) + fieldSize;
				alignment = Math.Max(alignment, fieldAlignment);
			}

			return (AlignUp(offset, alignment), alignment);
		}

		if (type is UnionTypeSymbol nestedUnion && nestedUnion.IsUnsafe)
			return GetRawUnionLayout(nestedUnion);

		if (type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt) || type.Equals(TypeSymbol.Float)) return (4, 4);
		if (type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong) || type.Equals(TypeSymbol.Double)) return (8, 8);
		if (type.Equals(TypeSymbol.NInt) || type.Equals(TypeSymbol.NUInt)) return (_nativePointerBytes, _nativePointerBytes);
		if (type.Equals(TypeSymbol.Short) || type.Equals(TypeSymbol.UShort)) return (2, 2);
		if (type.Equals(TypeSymbol.SByte) || type.Equals(TypeSymbol.Byte) || type.Equals(TypeSymbol.Bool) || type.Equals(TypeSymbol.Char)) return (1, 1);
		return (8, 8);
	}

	private static int AlignUp(int value, int alignment)
	{
		return (value + alignment - 1) / Math.Max(1, alignment) * Math.Max(1, alignment);
	}
}
