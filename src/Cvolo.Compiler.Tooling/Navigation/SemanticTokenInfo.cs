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

	/// <summary>
	/// The occurrence denotes a compiler-proven compile-time constant: an immutable
	/// top-level <c>global</c> (immutable storage), never a mutable global, a local
	/// <c>val</c>, a field, a property or an enum member.
	/// </summary>
	Constant = 1 << 3,

	/// <summary>
	/// The occurrence is the name of a compiler-proven imported <c>extern</c> function: a
	/// standalone <c>extern</c> declaration or a member of an importing <c>extern "C"</c>/
	/// <c>extern "system"</c> block whose implementation lives outside Cvolo. An <c>expose
	/// extern</c> export, a native-ABI (<c>unsafe "C"</c>) Cvolo function with a body, a
	/// delegate, a delegate-valued variable, a loader or a Cvolo wrapper never carries this.
	/// </summary>
	ExternFunction = 1 << 4,
}

/// <summary>
/// One semantic source occurrence: the exact identifier/operator span, its symbol classification
/// and its semantic modifier flags. Spans are absolute UTF-16 spans over the snapshot text.
/// </summary>
public sealed record SemanticTokenInfo(
	TextSpan Span,
	ToolingSymbolKind Kind,
	SemanticTokenModifiers Modifiers);
