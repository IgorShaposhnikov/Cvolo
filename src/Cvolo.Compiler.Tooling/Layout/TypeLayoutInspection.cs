namespace Cvolo.Compiler.Tooling;

/// <summary>Where a padding region sits inside a laid-out aggregate.</summary>
public enum ToolingPaddingKind
{
	/// <summary>Alignment padding introduced before a stored member.</summary>
	Internal,

	/// <summary>Padding after the last stored member, up to the aggregate's rounded-up size.</summary>
	Tail,
}

/// <summary>
/// One stored member of a laid-out aggregate: the byte offset, the size in bytes and the storage
/// alignment it requires. All three are compiler-produced.
/// </summary>
public sealed record TypeLayoutMemberInspection(
	string Name,
	string TypeDisplay,
	long Offset,
	long Size,
	long Alignment);

/// <summary>
/// One contiguous run of padding inside a laid-out aggregate.
/// </summary>
public sealed record TypeLayoutPaddingInspection(long Offset, long Size, ToolingPaddingKind Kind);

/// <summary>
/// The complete, target-aware natural object layout of a concrete type, as reported by the
/// compiler's layout service. This describes object/storage layout only; it makes no claim about
/// function-call ABI classification. Every number here is produced by the same computation that
/// backs <c>sizeof</c>, <c>alignof</c> and <c>offsetof</c>, so a client must never re-derive
/// offsets, sizes, padding or alignment.
/// </summary>
/// <param name="TypeDisplay">The inspected type, rendered from compiler-owned symbol names.</param>
/// <param name="TargetDisplay">The compilation target the layout was computed for.</param>
/// <param name="Size">Total object size in bytes, including internal and trailing padding.</param>
/// <param name="Alignment">Required storage alignment in bytes.</param>
/// <param name="PayloadSize">Bytes occupied by stored members, excluding padding.</param>
/// <param name="PaddingSize">Bytes occupied by padding, including any trailing padding.</param>
/// <param name="Stride">Distance between consecutive elements of an array, otherwise null.</param>
/// <param name="ElementCount">Element count of a fixed-size array, otherwise null.</param>
/// <param name="ElementSize">Size of one array element in bytes, otherwise null.</param>
/// <param name="ElementAlignment">Storage alignment of one array element in bytes, otherwise null.</param>
/// <param name="Members">Stored members in declaration order; union variants all start at offset zero.</param>
/// <param name="Padding">Padding regions in ascending offset order.</param>
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
	IReadOnlyList<TypeLayoutMemberInspection> Members,
	IReadOnlyList<TypeLayoutPaddingInspection> Padding);
