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
/// What a layout viewer may do with one member row, resolved by the compiler so the client never has
/// to: where the field name navigates, where the type name navigates, whether the field's type is an
/// aggregate with a layout of its own, and the field's own documentation. Every target is a
/// declaration the compiler already indexed; none of it is derived from the displayed text.
/// </summary>
/// <param name="Signature">The field as it was declared, qualified by the type that stores it.</param>
/// <param name="Documentation">The field's <c>///</c> documentation, when it has any.</param>
/// <param name="Definition">Where the field name is declared, or null when it is not source backed.</param>
/// <param name="TypeDefinition">Where the field's type is declared, or null when it has no source declaration.</param>
/// <param name="NestedLayout">
/// Where a nested layout of the field's own type can be requested, or null when the field's type is
/// not an aggregate the compiler can lay out as a type of its own.
/// </param>
public sealed record TypeLayoutMemberNavigation(
	string Signature,
	string? Documentation,
	SymbolDefinition? Definition,
	SymbolDefinition? TypeDefinition,
	SymbolDefinition? NestedLayout);

/// <summary>
/// One stored member of a laid-out aggregate: the byte offset, the size in bytes and the storage
/// alignment it requires. All three are compiler-produced.
/// </summary>
/// <param name="Navigation">
/// The compiler-resolved navigation facts for this member, or null when the member's declaration was
/// not reached from a source position that could be resolved.
/// </param>
public sealed record TypeLayoutMemberInspection(
	string Name,
	string TypeDisplay,
	long Offset,
	long Size,
	long Alignment,
	TypeLayoutMemberNavigation? Navigation = null);

/// <summary>
/// One contiguous run of padding inside a laid-out aggregate.
/// </summary>
public sealed record TypeLayoutPaddingInspection(long Offset, long Size, ToolingPaddingKind Kind);

/// <summary>
/// Where one stored field sits inside its containing type, as the compiler's layout service computed
/// it. This is the fact a field layout annotation shows, whether it is rendered as a CodeLens beside
/// the declaration or as an inlay hint after it; the two surfaces never compute it separately.
/// </summary>
/// <param name="ContainingTypeDisplay">The declared type whose layout contains this field.</param>
/// <param name="FieldName">The field's declared name.</param>
/// <param name="Offset">The field's byte offset inside the containing type.</param>
/// <param name="Size">The field's storage size in bytes.</param>
/// <param name="Alignment">The field's required alignment in bytes.</param>
/// <param name="PaddingBefore">
/// The alignment padding the compiler inserted immediately before this field, or zero when the
/// field starts the type or follows storage that needed no padding. Tail padding is not repeated
/// here: it belongs to the type summary and the detailed view, and is reported exactly once.
/// </param>
public sealed record ToolingFieldLayoutInfo(
	string ContainingTypeDisplay,
	string FieldName,
	long Offset,
	long Size,
	long Alignment,
	long PaddingBefore);

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
/// <param name="Definition">
/// Where the inspected type is declared, when the inspection was reached from a source position. The
/// type name in the viewer is navigable because the compiler owns this target; it is null rather than
/// guessed when the type has no source declaration.
/// </param>
/// <param name="Subject">
/// The canonical identity a client stores so the same type's layout can be requested again after the
/// project changes, without holding any snapshot-scoped symbol. The server resolves it against
/// whatever snapshot is current when the next request arrives, so a renamed or removed type simply
/// stops resolving rather than resurrecting a stale layout.
/// </param>
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
	IReadOnlyList<TypeLayoutPaddingInspection> Padding,
	SymbolDefinition? Definition = null,
	string? Subject = null);
