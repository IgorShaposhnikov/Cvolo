namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Identifies where a <see cref="Diagnostic"/> applies: a <see cref="DocumentId"/> plus a
/// <see cref="TextSpan"/> within that document's <see cref="SourceText"/>.
/// An optional <see cref="Message"/> may refine the location when offered by the producer.
/// </summary>
public sealed record DiagnosticLocation(DocumentId DocumentId, TextSpan Span, string? Message = null);
