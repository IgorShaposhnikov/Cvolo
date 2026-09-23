namespace Cvolo.Compiler.Tooling;

/// <summary>
/// One source occurrence semantically bound to a snapshot-scoped symbol.
/// </summary>
public sealed record SymbolReference(DocumentId DocumentId, TextSpan Span, bool IsDeclaration);
