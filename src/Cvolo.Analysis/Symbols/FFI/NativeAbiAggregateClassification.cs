using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Symbols.FFI;

/// <summary>
/// Separates semantic native-ABI admissibility from the target-specific aggregate lowering that
/// is performed by the LLVM emitter.
/// </summary>
/// <remarks>
/// The semantic pass deliberately admits only the aggregate shapes for which the backend has a
/// verified x64 lowering rule. Small integer-class aggregates up to one eightbyte are coerced to
/// an integer register. Aggregates larger than two eightbytes use the platform's indirect
/// parameter / sret path. The difficult 9..16 byte mixed/two-register class remains fail-closed.
/// This keeps unsupported signatures at CVLF2051 instead of silently using ordinary Cvolo ABI.
/// </remarks>
public static class NativeAbiAggregateClassification
{
	public static bool RequiresTargetClassification(TypeSymbol type)
	{
		return type is StructTypeSymbol || type is UnionTypeSymbol { IsUnsafe: true };
	}

	public static bool IsVerifiedForDirectBoundary(TypeSymbol type, int nativePointerBytes, bool targetSupported)
	{
		if (!RequiresTargetClassification(type))
			return true;

		// Fail closed unless both the pointer width and target family are in the backend's
		// verified aggregate-lowering set.
		if (!targetSupported || nativePointerBytes != 8)
			return false;

		var layout = GetNaturalLayout(type, nativePointerBytes, []);
		if (layout is null)
			return false;

		var (size, _) = layout.Value;
		if (size <= 8)
			return IsIntegerClassAggregate(type, []);

		// x64 Windows passes all non-power-of-two/>8 aggregates indirectly; x86-64 SysV passes
		// aggregates larger than 16 bytes indirectly.  We only claim the common verified large
		// path here, leaving 9..16 bytes fail-closed until the split-register classifier exists.
		return size > 16;
	}

	private static bool IsIntegerClassAggregate(TypeSymbol type, HashSet<TypeSymbol> active)
	{
		if (type is EnumTypeSymbol enumType)
			return IsIntegerClassAggregate(enumType.StorageType, active);
		if (type is PointerTypeSymbol or RawPointerTypeSymbol)
			return true;
		if (type is DelegateTypeSymbol delegateType)
			return delegateType.IsNative;
		if (type is ArrayTypeSymbol array)
			return IsIntegerClassAggregate(array.ElementType, active);
		if (type is UnionTypeSymbol union && union.IsUnsafe)
		{
			if (!active.Add(type))
			{
				return false;
			}

			try
			{
				// An INTEGER member dominates the SysV eightbyte class. Pure FP raw unions are left
				// unverified rather than guessing their SSE class.
				return union.Fields.Where(f => !f.IsVoidVariant)
					.Any(f => IsIntegerClassAggregate(f.Type, active));
			}
			finally
			{
				active.Remove(type);
			}
		}

		if (type is StructTypeSymbol structure)
		{
			if (!active.Add(type))
			{
				return false;
			}

			try
			{
				// Within one eightbyte, the presence of any integer/pointer class member makes the
				// aggregate INTEGER under SysV; Windows x64 also transports 1/2/4/8-byte structs in
				// an integer register. Pure floating aggregates remain fail-closed here.
				return structure.Fields.Any(f => IsIntegerClassAggregate(f.Type, active));
			}
			finally
			{
				active.Remove(type);
			}
		}

		return type.Equals(TypeSymbol.SByte) || type.Equals(TypeSymbol.Byte)
			|| type.Equals(TypeSymbol.Short) || type.Equals(TypeSymbol.UShort)
			|| type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt)
			|| type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong)
			|| type.Equals(TypeSymbol.NInt) || type.Equals(TypeSymbol.NUInt)
			|| type.Equals(TypeSymbol.Char) || type.Equals(TypeSymbol.Bool);
	}

	private static (int Size, int Alignment)? GetNaturalLayout(TypeSymbol type, int nativePointerBytes, HashSet<TypeSymbol> active)
	{
		if (type is EnumTypeSymbol enumType)
			return GetNaturalLayout(enumType.StorageType, nativePointerBytes, active);
		if (type is PointerTypeSymbol or RawPointerTypeSymbol)
			return (nativePointerBytes, nativePointerBytes);
		if (type is DelegateTypeSymbol delegateType)
			return delegateType.IsNative ? (nativePointerBytes, nativePointerBytes) : null;
		if (type is ArrayTypeSymbol array)
		{
			var element = GetNaturalLayout(array.ElementType, nativePointerBytes, active);
			return element is null ? null : (element.Value.Size * array.Size, element.Value.Alignment);
		}

		if (type is StructTypeSymbol structure)
		{
			if (!active.Add(type))
			{
				return null;
			}

			try
			{
				var offset = 0;
				var alignment = 1;
				foreach (var field in structure.Fields)
				{
					var fieldLayout = GetNaturalLayout(field.Type, nativePointerBytes, active);
					if (fieldLayout is null)
					{
						return null;
					}

					offset = AlignUp(offset, fieldLayout.Value.Alignment) + fieldLayout.Value.Size;
					alignment = Math.Max(alignment, fieldLayout.Value.Alignment);
				}

				return (AlignUp(offset, alignment), alignment);
			}
			finally
			{
				active.Remove(type);
			}
		}

		if (type is UnionTypeSymbol union && union.IsUnsafe)
		{
			if (!active.Add(type))
			{
				return null;
			}

			try
			{
				var size = 0;
				var alignment = 1;
				foreach (var field in union.Fields.Where(f => !f.IsVoidVariant))
				{
					var fieldLayout = GetNaturalLayout(field.Type, nativePointerBytes, active);
					if (fieldLayout is null)
					{
						return null;
					}

					size = Math.Max(size, fieldLayout.Value.Size);
					alignment = Math.Max(alignment, fieldLayout.Value.Alignment);
				}

				return (AlignUp(size, alignment), alignment);
			}
			finally
			{
				active.Remove(type);
			}
		}

		if (type.Equals(TypeSymbol.SByte) || type.Equals(TypeSymbol.Byte)
			|| type.Equals(TypeSymbol.Bool) || type.Equals(TypeSymbol.Char)) return (1, 1);
		if (type.Equals(TypeSymbol.Short) || type.Equals(TypeSymbol.UShort)) return (2, 2);
		if (type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt) || type.Equals(TypeSymbol.Float)) return (4, 4);
		if (type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong) || type.Equals(TypeSymbol.Double)) return (8, 8);
		if (type.Equals(TypeSymbol.NInt) || type.Equals(TypeSymbol.NUInt)) return (nativePointerBytes, nativePointerBytes);
		return null;
	}

	private static int AlignUp(int value, int alignment)
	{
		return ((value + alignment - 1) / Math.Max(1, alignment)) * Math.Max(1, alignment);
	}
}
