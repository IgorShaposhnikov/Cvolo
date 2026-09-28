namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Compiler-owned classification of one CodeLens. Independent of any editor protocol kind.
/// </summary>
public enum ToolingCodeLensKind
{
	/// <summary>
	/// A semantic reference count for one named declaration.
	/// </summary>
	References,
	/// <summary>
	/// A compact object/storage layout summary for one concrete type.
	/// </summary>
	Layout,
	/// <summary>
	/// Already-resolved native linkage identity for one declaration.
	/// </summary>
	NativeInterop,
}

/// <summary>
/// A semantic reference count for one declaration. <see cref="Count"/> is a real count and may be
/// zero: zero means "computed and not referenced", never "unavailable". Absence of this payload on a
/// <see cref="ToolingCodeLensInfo"/> is what means the count is not applicable.
/// </summary>
public sealed record ToolingReferenceCount(SymbolId Symbol, int Count);

/// <summary>
/// One compiler-computed CodeLens. <see cref="Title"/> is the presentation text the client renders
/// verbatim; the structured payload carries the same facts so a client never has to parse it.
/// </summary>
public sealed record ToolingCodeLensInfo(
	TextSpan Range,
	ToolingCodeLensKind Kind,
	string Title,
	SymbolId? SymbolId,
	TypeLayoutInspection? Layout,
	NativeLinkageInfo? NativeLinkage,
	ToolingReferenceCount? ReferenceCount = null,
	ToolingFieldLayoutInfo? FieldLayout = null);

/// <summary>
/// Which categories of CodeLens a caller wants. Defaults follow the increment's recommended
/// settings: reference counts and layout summaries on, member (field/variant) lenses off.
/// </summary>
public sealed record ToolingCodeLensOptions
{
	/// <summary>
	/// The recommended default set.
	/// </summary>
	public static ToolingCodeLensOptions Default { get; } = new();

	/// <summary>
	/// Emit reference-count lenses, including zero counts.
	/// </summary>
	public bool References { get; init; } = true;

	/// <summary>
	/// Emit compact layout lenses for concrete types.
	/// </summary>
	public bool Layout { get; init; } = true;

	/// <summary>
	/// Emit reference-count lenses for fields and enum variants as well, which is off by default
	/// because a large struct or enum would otherwise get a lens on every member.
	/// </summary>
	public bool Members { get; init; }

	/// <summary>
	/// Emit per-field layout lenses (offset, size, alignment and the padding before the field) for
	/// fields whose containing type has an authoritative layout. Off by default for the same reason
	/// as <see cref="Members"/>: one line per field is a deliberate choice, not the default.
	/// </summary>
	public bool FieldLayout { get; init; }

	/// <summary>
	/// Emit native-linkage lenses for declarations with resolved interop metadata.
	/// </summary>
	public bool NativeInterop { get; init; } = true;
}
