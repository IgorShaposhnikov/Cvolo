using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Layout;

/// <summary>
/// Natural object layout of a semantic type: the complete object size (including
/// internal and trailing padding) and the required storage alignment.
/// </summary>
public readonly record struct TypeLayout(long Size, long Alignment);

/// <summary>
/// The shared, target-aware semantic layout service. It owns the natural object-layout
/// rules used by <c>sizeof</c>, <c>alignof</c>, <c>offsetof</c> and by LLVM emission,
/// well before codegen. Layout is parameterized by the active native pointer width so it
/// never assumes the host process width implicitly.
/// </summary>
public sealed class TypeLayoutService(int nativePointerBytes)
{
	private readonly int _pointer = Math.Max(1, nativePointerBytes);

	public TypeLayout GetLayout(TypeSymbol type) => Compute(type, new HashSet<TypeSymbol>());

	/// <summary>
	/// Resolves the byte offset of a nested stored-member designator starting at
	/// <paramref name="aggregate"/>, or <c>null</c> when the path is not a valid stored layout.
	/// Each raw/unsafe union component contributes offset zero.
	/// </summary>
	public long? TryGetFieldOffset(TypeSymbol aggregate, IReadOnlyList<string> memberPath)
	{
		if (memberPath.Count == 0)
			return null;

		long offset = 0;
		var current = aggregate;
		foreach (var member in memberPath)
		{
			switch (current)
			{
				case StructTypeSymbol structType:
					{
						long fieldOffset = 0;
						StructFieldSymbol? match = null;
						foreach (var field in structType.Fields)
						{
							var (size, alignment) = GetLayout(field.Type);
							fieldOffset = AlignUp(fieldOffset, alignment);
							if (string.Equals(field.Name, member, StringComparison.Ordinal))
							{
								match = field;
								break;
							}

							fieldOffset += size;
						}

						if (match is null)
							return null;

						offset += fieldOffset;
						current = match.Type;
						break;
					}
				case UnionTypeSymbol unionType when unionType.IsUnsafe:
					{
						var field = unionType.FindField(member);
						if (field is null)
							return null;

						current = field.Type;
						break;
					}
				default:
					return null;
			}
		}

		return offset;
	}

	private TypeLayout Compute(TypeSymbol type, HashSet<TypeSymbol> active)
	{
		if (type is null)
			return new TypeLayout(4, 4);

		if (!active.Add(type))
			return new TypeLayout(4, 4);

		try
		{
			if (type.Equals(TypeSymbol.String) || type is PointerTypeSymbol or RawPointerTypeSymbol)
				return new TypeLayout(_pointer, _pointer);

			if (type is SliceTypeSymbol)
			{
				var alignment = Math.Max(_pointer, 4);
				return new TypeLayout(AlignUp(_pointer + 4, alignment), alignment);
			}

			if (type is DelegateTypeSymbol delegateType)
			{
				var words = delegateType.IsNative ? 1 : DelegateTypeSymbol.SafeDelegateWordCount;
				return new TypeLayout(words * _pointer, _pointer);
			}

			if (type is EnumTypeSymbol enumType)
				return Compute(enumType.StorageType, active);

			if (type is ArrayTypeSymbol arrayType)
			{
				var element = Compute(arrayType.ElementType, active);
				return new TypeLayout(element.Size * arrayType.Size, element.Alignment);
			}

			if (type is StructTypeSymbol structType)
			{
				long offset = 0;
				long alignment = 1;
				foreach (var field in structType.Fields)
				{
					var fieldLayout = Compute(field.Type, active);
					offset = AlignUp(offset, fieldLayout.Alignment) + fieldLayout.Size;
					alignment = Math.Max(alignment, fieldLayout.Alignment);
				}

				return new TypeLayout(AlignUp(offset, alignment), alignment);
			}

			if (type is UnionTypeSymbol unionType)
			{
				if (unionType.IsNpoEligible)
					return new TypeLayout(_pointer, _pointer);

				if (unionType.IsUnsafe)
				{
					long maxSize = 0;
					long maxAlignment = 1;
					foreach (var field in unionType.Fields)
					{
						if (field.IsVoidVariant)
							continue;

						var fieldLayout = Compute(field.Type, active);
						maxSize = Math.Max(maxSize, fieldLayout.Size);
						maxAlignment = Math.Max(maxAlignment, fieldLayout.Alignment);
					}

					return new TypeLayout(AlignUp(maxSize, maxAlignment), maxAlignment);
				}

				long maxPayload = 0;
				long payloadAlignment = 1;
				foreach (var field in unionType.Fields)
				{
					if (field.IsVoidVariant)
						continue;

					var fieldLayout = Compute(field.Type, active);
					maxPayload = Math.Max(maxPayload, fieldLayout.Size);
					payloadAlignment = Math.Max(payloadAlignment, fieldLayout.Alignment);
				}

				return new TypeLayout(1 + maxPayload, payloadAlignment);
			}

			if (type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt) || type.Equals(TypeSymbol.Float)) return new TypeLayout(4, 4);
			if (type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong) || type.Equals(TypeSymbol.Double)) return new TypeLayout(8, 8);
			if (type.Equals(TypeSymbol.NInt) || type.Equals(TypeSymbol.NUInt)) return new TypeLayout(_pointer, _pointer);
			if (type.Equals(TypeSymbol.Short) || type.Equals(TypeSymbol.UShort)) return new TypeLayout(2, 2);
			if (type.Equals(TypeSymbol.SByte) || type.Equals(TypeSymbol.Byte) || type.Equals(TypeSymbol.Bool) || type.Equals(TypeSymbol.Char)) return new TypeLayout(1, 1);

			return new TypeLayout(4, 4);
		}
		finally
		{
			active.Remove(type);
		}
	}

	private static long AlignUp(long value, long alignment)
	{
		var divisor = Math.Max(1, alignment);
		return (value + divisor - 1) / divisor * divisor;
	}
}
