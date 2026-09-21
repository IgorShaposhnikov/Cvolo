using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Emitter.LLVM.Codegen.TypeLowering;

/// <summary>
/// Provides shared aggregate layout helpers used by LLVM emission without changing the compiler's
/// existing layout semantics.
/// </summary>
/// <remarks>
/// This class centralizes field/variant indexing and the compiler's current semantic byte-size
/// calculation. The size calculation intentionally preserves the pre-refactor behavior, including
/// the current 64-bit pointer assumptions and simple packed-size arithmetic. Target-specific native
/// ABI classification and alignment-aware layout remain separate concerns for a later ABI increment.
/// </remarks>
internal sealed class AggregateLayout
{
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
	/// This intentionally mirrors the legacy <c>CodeGenerator.GetByteSize</c> implementation rather
	/// than introducing target-aware alignment or ABI fixes during a mechanical refactor.
	/// </remarks>
	public int GetByteSize(TypeSymbol type)
	{
		if (type is null) return 0;
		if (type.Equals(TypeSymbol.String) || type is PointerTypeSymbol or RawPointerTypeSymbol) return 8;
		if (type is SliceTypeSymbol) return 16;
		if (type is DelegateTypeSymbol) return 16;
		if (type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt) || type.Equals(TypeSymbol.Float)) return 4;
		if (type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong) || type.Equals(TypeSymbol.NInt) || type.Equals(TypeSymbol.NUInt) || type.Equals(TypeSymbol.Double)) return 8;
		if (type.Equals(TypeSymbol.Short) || type.Equals(TypeSymbol.UShort)) return 2;
		if (type.Equals(TypeSymbol.SByte) || type.Equals(TypeSymbol.Byte) || type.Equals(TypeSymbol.Bool) || type.Equals(TypeSymbol.Char)) return 1;
		if (type is ArrayTypeSymbol arrayType) return GetByteSize(arrayType.ElementType) * arrayType.Size;

		if (type is StructTypeSymbol structType)
		{
			var size = 0;
			foreach (var field in structType.Fields)
				size += GetByteSize(field.Type);
			return size;
		}

		if (type is UnionTypeSymbol unionType)
		{
			if (unionType.IsNpoEligible)
				return 8;

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
}
