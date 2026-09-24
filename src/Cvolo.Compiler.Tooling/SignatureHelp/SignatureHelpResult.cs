namespace Cvolo.Compiler.Tooling.SignatureHelp;

/// <summary>
/// A UTF-16 code-unit span inside a signature label that marks one parameter. Offsets are always
/// measured against the label of the owning <see cref="SignatureCandidateInfo"/> (display spans,
/// never source spans). Start must be non-negative, Length positive, and Start + Length must not
/// exceed the owning label's length.
/// </summary>
public readonly record struct SignatureLabelSpan(int Start, int Length);

/// <summary>
/// One parameter of a callable signature: where its name appears in the signature label, plus
/// optional compiler-owned plain-text documentation.
/// </summary>
public sealed record SignatureParameterInfo(SignatureLabelSpan LabelSpan, string? Documentation = null);

/// <summary>
/// One callable signature candidate: a compiler-owned display label suitable for direct signature-help
/// presentation, optional documentation, parameter label spans, and — for the resolved signature only —
/// the zero-based active parameter index (null when the signature has no parameters).
/// </summary>
public sealed record SignatureCandidateInfo(
	string Label,
	string? Documentation,
	IReadOnlyList<SignatureParameterInfo> Parameters,
	int? ActiveParameter);

/// <summary>
/// Signature-help response for a call at the cursor position. When the resolved callable belongs to an
/// overload group, every overload of that group appears in declaration order;
/// <see cref="ActiveSignature"/> selects the semantically resolved one.
/// </summary>
public sealed record SignatureHelpInfo(
	IReadOnlyList<SignatureCandidateInfo> Signatures,
	int ActiveSignature);