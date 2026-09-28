namespace Cvolo.Compiler.Tooling;

/// <summary>
/// How one occurrence uses the symbol it names, as classified by the compiler. This is a semantic
/// fact; no editor infers it from the syntactic position of the occurrence.
/// </summary>
public enum ReferenceAccessKind
{
	/// <summary>The occurrence only reads the value.</summary>
	Read,

	/// <summary>The occurrence only overwrites the value.</summary>
	Write,

	/// <summary>The occurrence both reads and overwrites the value, as in a compound assignment.</summary>
	ReadWrite,

	/// <summary>The occurrence is the declaration's own name.</summary>
	Declaration,
}

/// <summary>
/// One semantically resolved occurrence of the symbol under the caret, in the current document.
/// Occurrences are reported without a textual same-spelling fallback: a symbol that is not bound
/// at the caret produces no highlights at all.
/// </summary>
public sealed record ToolingDocumentHighlight(TextSpan Range, ReferenceAccessKind AccessKind);
