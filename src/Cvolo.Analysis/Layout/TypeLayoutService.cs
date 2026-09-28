using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Layout;

/// <summary>
/// Natural object layout of a semantic type: the complete object size (including
/// internal and trailing padding) and the required storage alignment.
/// </summary>
public readonly record struct TypeLayout(long Size, long Alignment);

/// <summary>Where a padding region sits inside a laid-out aggregate.</summary>
public enum TypeLayoutPaddingKind
{
	/// <summary>Alignment padding introduced before a stored member.</summary>
	Internal,

	/// <summary>Padding after the last stored member, up to the aggregate's rounded-up size.</summary>
	Tail,
}

/// <summary>One stored member of a laid-out aggregate.</summary>
public readonly record struct TypeLayoutMemberInfo(
	string Name,
	string TypeDisplay,
	long Offset,
	long Size,
	long Alignment);

/// <summary>One contiguous run of padding inside a laid-out aggregate.</summary>
public readonly record struct TypeLayoutPaddingInfo(long Offset, long Size, TypeLayoutPaddingKind Kind);

/// <summary>
/// The complete, target-aware natural object layout of a concrete type. This is the single
/// authoritative description of storage layout: it is the same computation behind
/// <c>sizeof</c>, <c>alignof</c>, <c>offsetof</c> and LLVM emission, exposed with the member and
/// padding detail that editors and tooling present. Nothing downstream re-derives sizes,
/// alignments, offsets or padding.
/// </summary>
public sealed record TypeLayoutInspection(
	string TypeDisplay,
	string TargetDisplay,
	long Size,
	long Alignment,
	long PayloadSize,
	long PaddingSize,
	long? Stride,
	long? ElementCount,
	long? ElementSize,
	long? ElementAlignment,
	IReadOnlyList<TypeLayoutMemberInfo> Members,
	IReadOnlyList<TypeLayoutPaddingInfo> Padding);

/// <summary>
/// The shared, target-aware semantic layout service. It owns the natural object-layout
/// rules used by <c>sizeof</c>, <c>alignof</c>, <c>offsetof</c> and by LLVM emission,
/// well before codegen. Layout is parameterized by the active native pointer width so it
/// never assumes the host process width implicitly.
/// </summary>
public sealed class TypeLayoutService(int nativePointerBytes, string? targetDisplay = null)
{
	private readonly int _pointer = Math.Max(1, nativePointerBytes);

	/// <summary>
	/// The identity of the compilation target this service computes layout for. Layout results
	/// carry it so a consumer can tell which target a number belongs to; a target change replaces
	/// the service and therefore invalidates every prior result.
	/// </summary>
	public string TargetDisplay { get; } = string.IsNullOrEmpty(targetDisplay) ? "host" : targetDisplay;

	public TypeLayout GetLayout(TypeSymbol type)
	{
		var node = Compute(type, new HashSet<TypeSymbol>());
		return new TypeLayout(node.Size, node.Alignment);
	}

	/// <summary>
	/// The full inspection of <paramref name="type"/>: the compact size/alignment facts plus the
	/// member offsets, member sizes, member alignments and padding regions they imply. The
	/// numeric facts are produced by the same computation as <see cref="GetLayout"/>, so the
	/// inspection can never disagree with <c>sizeof</c>/<c>alignof</c>/<c>offsetof</c> or codegen.
	/// </summary>
	public TypeLayoutInspection Inspect(TypeSymbol type)
	{
		var node = Compute(type, new HashSet<TypeSymbol>());
		long? stride = null;
		long? elementSize = null;
		long? elementAlignment = null;

		// An array has no stored members of its own, so its element facts are reported separately:
		// the editor's detailed view shows element size/alignment next to the stride and the count
		// instead of pretending the first element is a member at offset zero.
		if (type is ArrayTypeSymbol array && array.Size > 0)
		{
			var element = Compute(array.ElementType, new HashSet<TypeSymbol>());
			stride = node.Size / array.Size;
			elementSize = element.Size;
			elementAlignment = element.Alignment;
		}

		return new TypeLayoutInspection(
			type?.Name ?? "void",
			TargetDisplay,
			node.Size,
			node.Alignment,
			node.PayloadSize,
			node.Size - node.PayloadSize,
			stride,
			type is ArrayTypeSymbol counted ? counted.Size : null,
			elementSize,
			elementAlignment,
			node.Members,
			node.Padding);
	}

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

	private Node Compute(TypeSymbol type, HashSet<TypeSymbol> active)
	{
		if (type is null)
			return Node.Opaque(4, 4);

		if (!active.Add(type))
			return Node.Opaque(4, 4);

		try
		{
			if (type.Equals(TypeSymbol.String) || type is PointerTypeSymbol or RawPointerTypeSymbol)
				return Node.Opaque(_pointer, _pointer);

			if (type is SliceTypeSymbol)
			{
				var alignment = Math.Max(_pointer, 4);
				return Node.Opaque(AlignUp(_pointer + 4, alignment), alignment);
			}

			if (type is DelegateTypeSymbol delegateType)
			{
				var words = delegateType.IsNative ? 1 : DelegateTypeSymbol.SafeDelegateWordCount;
				return Node.Opaque(words * _pointer, _pointer);
			}

			if (type is EnumTypeSymbol enumType)
			{
				var storage = Compute(enumType.StorageType, active);
				return new Node(storage.Size, storage.Alignment, storage.Size, [], []);
			}

			if (type is ArrayTypeSymbol arrayType)
			{
				var element = Compute(arrayType.ElementType, active);
				return Node.Opaque(element.Size * arrayType.Size, element.Alignment);
			}

			if (type is StructTypeSymbol structType)
			{
				long offset = 0;
				long alignment = 1;
				long payload = 0;
				var members = new List<TypeLayoutMemberInfo>();
				var padding = new List<TypeLayoutPaddingInfo>();

				foreach (var field in structType.Fields)
				{
					var fieldLayout = Compute(field.Type, active);
					var aligned = AlignUp(offset, fieldLayout.Alignment);
					if (aligned > offset)
						padding.Add(new TypeLayoutPaddingInfo(offset, aligned - offset, TypeLayoutPaddingKind.Internal));

					members.Add(new TypeLayoutMemberInfo(field.Name, field.Type.Name, aligned, fieldLayout.Size, fieldLayout.Alignment));
					offset = aligned + fieldLayout.Size;
					payload += fieldLayout.Size;
					alignment = Math.Max(alignment, fieldLayout.Alignment);
				}

				var size = AlignUp(offset, alignment);
				if (size > offset)
					padding.Add(new TypeLayoutPaddingInfo(offset, size - offset, TypeLayoutPaddingKind.Tail));

				return new Node(size, alignment, payload, members, padding);
			}

			if (type is UnionTypeSymbol unionType)
			{
				if (unionType.IsNpoEligible)
					return Node.Opaque(_pointer, _pointer);

				long maxSize = 0;
				long maxAlignment = 1;
				long payload = 0;
				var members = new List<TypeLayoutMemberInfo>();
				var padding = new List<TypeLayoutPaddingInfo>();

				foreach (var field in unionType.Fields)
				{
					if (field.IsVoidVariant)
						continue;

					var fieldLayout = Compute(field.Type, active);
					members.Add(new TypeLayoutMemberInfo(field.Name, field.Type.Name, 0, fieldLayout.Size, fieldLayout.Alignment));
					maxSize = Math.Max(maxSize, fieldLayout.Size);
					maxAlignment = Math.Max(maxAlignment, fieldLayout.Alignment);
				}

				if (unionType.IsUnsafe)
				{
					// Every variant starts at offset zero, so the only unused bytes are the tail
					// of the largest variant.
					payload = maxSize;
					var size = AlignUp(maxSize, maxAlignment);
					if (size > payload)
						padding.Add(new TypeLayoutPaddingInfo(payload, size - payload, TypeLayoutPaddingKind.Tail));

					return new Node(size, maxAlignment, payload, members, padding);
				}

				// A safe union stores a one-byte tag ahead of the largest variant's payload.
				payload = 1 + maxSize;
				var safeMembers = new List<TypeLayoutMemberInfo>(members.Count + 1)
				{
					new("tag", "byte", 0, 1, 1),
				};

				for (var index = 0; index < members.Count; index++)
				{
					var member = members[index];
					safeMembers.Add(member with { Offset = 1 });
				}

				return new Node(1 + maxSize, maxAlignment, payload, safeMembers, []);
			}

			if (type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt) || type.Equals(TypeSymbol.Float)) return Node.Opaque(4, 4);
			if (type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong) || type.Equals(TypeSymbol.Double)) return Node.Opaque(8, 8);
			if (type.Equals(TypeSymbol.NInt) || type.Equals(TypeSymbol.NUInt)) return Node.Opaque(_pointer, _pointer);
			if (type.Equals(TypeSymbol.Short) || type.Equals(TypeSymbol.UShort)) return Node.Opaque(2, 2);
			if (type.Equals(TypeSymbol.SByte) || type.Equals(TypeSymbol.Byte) || type.Equals(TypeSymbol.Bool) || type.Equals(TypeSymbol.Char)) return Node.Opaque(1, 1);

			return Node.Opaque(4, 4);
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

	/// <summary>
	/// The one internal layout result. <see cref="Size"/> and <see cref="Alignment"/> are the
	/// numbers every consumer of layout agrees on; the remaining members describe where those
	/// bytes come from so the same computation can answer "what is at this offset" without a
	/// second, divergent calculation.
	/// </summary>
	private readonly record struct Node(
		long Size,
		long Alignment,
		long PayloadSize,
		IReadOnlyList<TypeLayoutMemberInfo> Members,
		IReadOnlyList<TypeLayoutPaddingInfo> Padding)
	{
		private static readonly IReadOnlyList<TypeLayoutMemberInfo> NoMembers = [];
		private static readonly IReadOnlyList<TypeLayoutPaddingInfo> NoPadding = [];

		/// <summary>
		/// A type whose bytes are not decomposed into source-visible members: a primitive, a
		/// pointer, a slice, a delegate, an unresolved template or a null type. All of its bytes
		/// count as payload because there is no member boundary that introduces padding.
		/// </summary>
		public static Node Opaque(long size, long alignment) => new(size, alignment, size, NoMembers, NoPadding);
	}
}
