namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Compiler-owned classification of one inlay hint. Independent of any editor protocol kind.
/// </summary>
public enum ToolingInlayHintKind
{
	/// <summary>The inferred type of a declaration whose source omits it.</summary>
	Type,

	/// <summary>The name of the parameter an argument position binds to.</summary>
	Parameter,

	/// <summary>The receiver mutability of an extension function whose source omits it.</summary>
	ReceiverMutability,

	/// <summary>Offset and size of a field, from the compiler's layout service.</summary>
	Layout,

	/// <summary>The compiler-selected value of an enum variant.</summary>
	EnumValue,

	/// <summary>An inferred generic type argument.</summary>
	GenericArgument,
}

/// <summary>
/// One presentation-only inlay hint. The label is compiler-owned text; the hint never implies a
/// rewrite of the source and never means the equivalent syntax is valid Cvolo.
/// </summary>
/// <param name="Position">Zero-based UTF-16 offset the label is anchored at.</param>
/// <param name="Kind">What the label describes.</param>
/// <param name="Label">The text to render.</param>
/// <param name="PaddingLeft">Whether the client should pad the label on its left.</param>
/// <param name="PaddingRight">Whether the client should pad the label on its right.</param>
/// <param name="RelatedSymbol">The symbol the label was derived from, when there is exactly one.</param>
public sealed record ToolingInlayHint(
	int Position,
	ToolingInlayHintKind Kind,
	string Label,
	bool PaddingLeft,
	bool PaddingRight = false,
	SymbolId? RelatedSymbol = null);

/// <summary>
/// Which categories of inlay hint a caller wants. The defaults follow the increment's recommended
/// settings: the three useful-by-default categories on, and the visually dense ones off.
/// </summary>
public sealed record ToolingInlayHintOptions
{
	/// <summary>
	/// The recommended default set.
	/// </summary>
	public static ToolingInlayHintOptions Default { get; } = new();

	/// <summary>
	/// Show the inferred type of a declaration whose source omits it.
	/// </summary>
	public bool Types { get; init; } = true;

	/// <summary>
	/// Show the parameter name each argument position binds to.
	/// </summary>
	public bool Parameters { get; init; } = true;

	/// <summary>
	/// Show the receiver mutability of an extension function whose source omits it.
	/// </summary>
	public bool ReceiverMutability { get; init; } = true;

	/// <summary>
	/// Show field offsets and sizes. Off by default because the annotations are visually dense.
	/// </summary>
	public bool Layout { get; init; }

	/// <summary>
	/// Show the compiler-selected value of an enum variant. Off by default.
	/// </summary>
	public bool EnumValues { get; init; }

	/// <summary>
	/// Show inferred generic type arguments. Off by default.
	/// </summary>
	public bool GenericArguments { get; init; }
}
