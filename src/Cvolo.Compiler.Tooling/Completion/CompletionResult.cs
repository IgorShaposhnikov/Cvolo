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
/// The fields of a completion candidate that can be resolved lazily on demand. The mask is a
/// property of the candidate; the language-server layer intersects it with the client's advertised
/// resolver support and strips fields already populated in the initial response.
/// </summary>
[Flags]
public enum CompletionResolvableFields
{
	/// <summary>No field can be resolved on demand.</summary>
	None = 0,
	/// <summary>The candidate's detail can be resolved on demand.</summary>
	Detail = 1 << 0,
	/// <summary>The candidate's plain-text documentation can be resolved on demand.</summary>
	Documentation = 1 << 1,
}

/// <summary>
/// A snapshot-scoped identity for a completion candidate. Values are only meaningful against the
/// snapshot they were produced from: resolving a foreign or stale id deterministically yields no
/// result rather than an error. The id is opaque to editors and must never cross a language-server
/// wire boundary in this form.
/// </summary>
public readonly record struct CompletionItemId
{
	internal Guid SnapshotToken { get; }
	internal int Value { get; }

	internal CompletionItemId(Guid snapshotToken, int value)
	{
		SnapshotToken = snapshotToken;
		Value = value;
	}

	public override string ToString() => $"completion-item:{Value}";
}

/// <summary>
/// Base for the structured, compiler-owned pieces of a callable insertion template. The segments do
/// not contain any snippet syntax: tab-stop numbering, escaping, and <c>$0</c> encoding are the
/// language-server layer's responsibilities (and are only applied when the client supports snippets).
/// </summary>
public abstract record CompletionInsertSegment;

/// <summary>
/// A fixed run of source text within an insertion template (for example the callable name).
/// </summary>
public sealed record CompletionLiteral(string Text) : CompletionInsertSegment;

/// <summary>
/// A named argument slot in a callable insertion template.
/// </summary>
public sealed record CompletionPlaceholder(string DefaultText) : CompletionInsertSegment;

/// <summary>
/// The final caret position of a callable insertion template. At most one may appear, and when the
/// client supports snippets it is encoded as <c>$0</c>.
/// </summary>
public sealed record CompletionFinalCursor : CompletionInsertSegment;

/// <summary>
/// A structured callable insertion template: the exact source the editor should produce when the
/// candidate is inserted, described in compiler-owned semantic pieces with no snippet syntax.
/// Segments are compared by value (in order) so identical templates from two analyses are equal.
/// </summary>
public sealed record CompletionInsertionPlan(IReadOnlyList<CompletionInsertSegment> SnippetSegments)
{
	public bool Equals(CompletionInsertionPlan? other)
		=> other is not null && SnippetSegments.SequenceEqual(other.SnippetSegments);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (var segment in SnippetSegments)
			hash.Add(segment);
		return hash.ToHashCode();
	}
}

/// <summary>
/// A single completion candidate. The <see cref="Label"/> is the display text; for callable
/// overloads several candidates share a label and are distinguished by <see cref="Detail"/>. Text is
/// always carried as the single normative plain <see cref="PlainInsertText"/>; when the client
/// supports snippets and a <see cref="InsertionPlan"/> is present, the protocol layer encodes that
/// plan as a snippet instead. <see cref="ItemId"/> is non-null only for callable candidates that can
/// be resolved on demand, and is null for keywords and other synthetic candidates.
/// </summary>
public sealed record CompletionCandidate(
	CompletionItemId? ItemId,
	string Label,
	string PlainInsertText,
	CompletionKind Kind,
	string? Detail,
	CompletionInsertionPlan? InsertionPlan,
	CompletionResolvableFields ResolvableFields);

/// <summary>
/// The result of a completion query: the range of source that a pending edit replaces (the typed
/// prefix or a zero-length span), plus the ordered candidate list (overloads remain distinct).
/// </summary>
public sealed record CompletionResult(TextSpan ReplacementRange, IReadOnlyList<CompletionCandidate> Candidates);

/// <summary>
/// The lazily-resolvable fields of a callable completion candidate. Either or both may be null (for
/// example a candidate without documentation resolves to a non-null <see cref="Detail"/> with a null
/// <see cref="Documentation"/>). Resolution never recomputes the candidate's insertion text.
/// </summary>
public sealed record CompletionResolvedInfo(string? Detail, string? Documentation);
