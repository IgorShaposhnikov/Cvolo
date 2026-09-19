namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Backend-neutral semantic-token modifier flags (LSP-5 §10). The protocol layer maps these to the
/// negotiated LSP modifier bitset; compiler semantics own the values.
/// </summary>
[Flags]
public enum SemanticTokenModifiers
{
	None = 0,
	Declaration = 1 << 0,
	Readonly = 1 << 1,
	Static = 1 << 2,
}

/// <summary>
/// One semantic source occurrence: the exact identifier/operator span, its symbol classification
/// and its semantic modifier flags. Spans are absolute UTF-16 spans over the snapshot text.
/// </summary>
public sealed record SemanticTokenInfo(
	TextSpan Span,
	ToolingSymbolKind Kind,
	SemanticTokenModifiers Modifiers);
