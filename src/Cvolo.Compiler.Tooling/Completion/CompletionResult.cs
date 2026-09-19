namespace Cvolo.Compiler.Tooling.Completion;

/// <summary>
/// The semantic kind of a completion candidate. Mirrors the analysis-side classification while
/// remaining a tooling-owned DTO (Language Server consumers map it to their own neutral kinds).
/// </summary>
public enum CompletionKind
{
	/// <summary>
	/// A local variable visible at the completion position.
	/// </summary>
	Local,
	/// <summary>
	/// A function parameter visible at the completion position.
	/// </summary>
	Parameter,
	/// <summary>
	/// A global variable visible from the completion position.
	/// </summary>
	Global,
	/// <summary>
	/// A free function callable from the completion position.
	/// </summary>
	Function,
	/// <summary>
	/// An extension method on the resolved receiver type.
	/// </summary>
	Method,
	/// <summary>
	/// A type or type alias visible from the completion position.
	/// </summary>
	Type,
	/// <summary>
	/// A namespace visible from the completion position.
	/// </summary>
	Namespace,
	/// <summary>
	/// A struct field of the resolved receiver type.
	/// </summary>
	StructField,
	/// <summary>
	/// A union variant of the resolved receiver type.
	/// </summary>
	UnionVariant,
	/// <summary>
	/// An enum variant of the resolved receiver type.
	/// </summary>
	EnumVariant,
	/// <summary>
	/// An enum metadata member (Min, Max, Count, Values) of the resolved receiver type.
	/// </summary>
	EnumMetadata,
	/// <summary>
	/// The Length member of a slice or array receiver.</summary>
	ArrayLength,
	/// <summary>
	/// A reserved keyword valid in the completion context.
	/// </summary>
	Keyword,
}

/// <summary>
/// A single completion item: the <see cref="Label"/> to display, the text to insert, and its
/// <see cref="Kind"/>. When <see cref="IsSnippet"/> is true, <see cref="InsertText"/> is an LSP
/// snippet body (with tab stops such as <c>$0</c>) rather than plain text.
/// </summary>
public sealed record CompletionCandidate(string Label, string InsertText, CompletionKind Kind, bool IsSnippet = false);

/// <summary>
/// The result of a completion query: the range of source that a pending edit replaces (the typed
/// prefix or a zero-length span), plus the ordered, deduplicated candidate list.
/// </summary>
public sealed record CompletionResult(TextSpan ReplacementRange, IReadOnlyList<CompletionCandidate> Candidates);
