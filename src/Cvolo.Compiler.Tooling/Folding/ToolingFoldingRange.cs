namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Compiler-owned classification of a foldable region. Independent of any editor protocol kind.
/// </summary>
public enum ToolingFoldingKind
{
	/// <summary>A syntactic region such as a body, block or declaration list.</summary>
	None,

	/// <summary>A multi-line comment region.</summary>
	Comment,

	/// <summary>A contiguous run of <c>///</c> documentation lines.</summary>
	Documentation,
}

/// <summary>
/// One foldable region, derived from the document's syntax tree rather than from scanning braces.
/// The range covers the whole region including its delimiters, and a client folds everything after
/// the start of the range up to its end. A region that cannot be expressed as a valid, non-inverted
/// range inside the document is never reported.
/// </summary>
public sealed record ToolingFoldingRange(TextSpan Range, ToolingFoldingKind Kind = ToolingFoldingKind.None);
