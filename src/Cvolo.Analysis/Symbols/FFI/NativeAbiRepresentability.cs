using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Symbols.FFI;

/// <summary>
/// Fail-closed predicate answering whether a type is representable verbatim at a given
/// position of the C ABI boundary. Aggregate/data positions are validated transitively by value;
/// raw pointers and native delegates terminate that traversal because only one pointer word is
/// embedded.
/// </summary>
public static class NativeAbiRepresentability
{
	public static bool IsNativeAbiRepresentable(TypeSymbol type, NativeAbiPosition position)
	{
		return IsNativeAbiRepresentable(type, position, []);
	}

	private static bool IsNativeAbiRepresentable(TypeSymbol type, NativeAbiPosition position, HashSet<TypeSymbol> activeByValue)
	{
		switch (type)
		{
			case var t when ReferenceEquals(t, TypeSymbol.Void):
				return position == NativeAbiPosition.Return;

			case var t when ReferenceEquals(t, TypeSymbol.Bool)
				|| ReferenceEquals(t, TypeSymbol.SByte)
				|| ReferenceEquals(t, TypeSymbol.Byte)
				|| ReferenceEquals(t, TypeSymbol.Short)
				|| ReferenceEquals(t, TypeSymbol.UShort)
				|| ReferenceEquals(t, TypeSymbol.Int)
				|| ReferenceEquals(t, TypeSymbol.UInt)
				|| ReferenceEquals(t, TypeSymbol.Long)
				|| ReferenceEquals(t, TypeSymbol.ULong)
				|| ReferenceEquals(t, TypeSymbol.NInt)
				|| ReferenceEquals(t, TypeSymbol.NUInt)
				|| ReferenceEquals(t, TypeSymbol.Float)
				|| ReferenceEquals(t, TypeSymbol.Double)
				|| ReferenceEquals(t, TypeSymbol.Char):
				return true;

			case var t when ReferenceEquals(t, TypeSymbol.String):
				return position == NativeAbiPosition.ImportedExternParameter;

			case RawPointerTypeSymbol:
				return true;

			case ArrayTypeSymbol array:
				if (position is NativeAbiPosition.Parameter or NativeAbiPosition.Return or NativeAbiPosition.ImportedExternParameter)
					return false;
				return IsNativeAbiRepresentable(array.ElementType, NativeAbiPosition.AggregateField, activeByValue);

			case EnumTypeSymbol enumType:
				return enumType.HasExplicitStorageType
					&& IsNativeAbiRepresentable(enumType.StorageType, NativeAbiPosition.AggregateField, activeByValue);

			case DelegateTypeSymbol { IsNative: true } del:
				return !string.IsNullOrEmpty(del.CallingConvention);

			case DelegateTypeSymbol:
				return false;

			case StructTypeSymbol str:
				return !str.HasDestructor && IsAbiSafeStruct(str, activeByValue);

			case UnionTypeSymbol { IsUnsafe: true } rawUnion:
				return IsAbiSafeRawUnion(rawUnion, activeByValue);

			default:
				return false;
		}
	}

	public static bool ContainsEnumWithoutExplicitStorage(TypeSymbol type)
		=> ContainsEnumWithoutExplicitStorage(type, []);

	private static bool ContainsEnumWithoutExplicitStorage(TypeSymbol type, HashSet<TypeSymbol> active)
	{
		switch (type)
		{
			case EnumTypeSymbol e: return !e.HasExplicitStorageType;
			case ArrayTypeSymbol a: return ContainsEnumWithoutExplicitStorage(a.ElementType, active);
			case StructTypeSymbol s:
				if (!active.Add(s))
				{
					return false;
				}

				try
				{
					return s.Fields.Any(f => ContainsEnumWithoutExplicitStorage(f.Type, active));
				}
				finally
				{
					active.Remove(s);
				}
			case UnionTypeSymbol { IsUnsafe: true } u:
				if (!active.Add(u))
				{
					return false;
				}

				try
				{
					return u.Fields.Where(f => !f.IsVoidVariant).Any(f => ContainsEnumWithoutExplicitStorage(f.Type, active));
				}
				finally
				{
					active.Remove(u);
				}
			default:
				return false;
		}
	}

	public static bool IsAbiSafeStruct(StructTypeSymbol str) => !NativeAbiSemanticSafety.ContainsResourceBearingValue(str) && IsAbiSafeStruct(str, []);

	private static bool IsAbiSafeStruct(StructTypeSymbol str, HashSet<TypeSymbol> activeByValue)
	{
		if (str.HasDestructor)
		{
			return false;
		}
		// A recursive by-value aggregate has no finite C object layout. Pointer recursion never
		// reaches this branch because RawPointerTypeSymbol terminates the traversal above.
		if (!activeByValue.Add(str))
		{
			return false;
		}

		try
		{
			foreach (var field in str.Fields)
			{
				if (!IsNativeAbiRepresentable(field.Type, NativeAbiPosition.AggregateField, activeByValue))
				{
					return false;
				}
			}

			return true;
		}
		finally
		{
			activeByValue.Remove(str);
		}
	}

	public static bool IsAbiSafeRawUnion(UnionTypeSymbol union) => !NativeAbiSemanticSafety.ContainsResourceBearingValue(union) && IsAbiSafeRawUnion(union, []);

	private static bool IsAbiSafeRawUnion(UnionTypeSymbol union, HashSet<TypeSymbol> activeByValue)
	{
		if (union.Fields.Count == 0 || !activeByValue.Add(union))
		{
			return false;
		}

		try
		{
			foreach (var field in union.Fields)
			{
				if (field.IsVoidVariant
					|| !IsNativeAbiRepresentable(field.Type, NativeAbiPosition.RawUnionField, activeByValue))
				{
					return false;
				}
			}

			return true;
		}
		finally
		{
			activeByValue.Remove(union);
		}
	}
}
