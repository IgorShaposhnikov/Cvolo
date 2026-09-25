namespace Cvolo.Compiler.Tooling;

/// <summary>
/// A compiler-provided quick fix candidate for one or more diagnostics in the same document.
/// </summary>
public sealed record CodeFixInfo(CodeFixId Id, string Title, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// One source edit produced by resolving a code fix. An empty span is an insertion and an empty
/// replacement string is a deletion.
/// </summary>
public sealed record CodeFixEdit(DocumentId Document, TextSpan Span, string NewText);

/// <summary>
/// Result of resolving a compiler-provided code fix against a snapshot.
/// </summary>
public abstract record CodeFixResolution;

/// <summary>
/// A complete, all-or-nothing set of source edits for a code fix.
/// </summary>
public sealed record CodeFixSuccess(IReadOnlyList<CodeFixEdit> Edits) : CodeFixResolution;

/// <summary>
/// A code fix that could not be resolved against the requested snapshot.
/// </summary>
public sealed record CodeFixFailure(string Message) : CodeFixResolution;
