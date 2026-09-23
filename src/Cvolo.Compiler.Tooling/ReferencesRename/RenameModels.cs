namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Compiler-owned preparation information for a rename request.
/// </summary>
public sealed record RenamePreparation(SymbolId SymbolId, TextSpan SubjectSpan, string Placeholder);

/// <summary>
/// One source replacement in a semantic rename plan.
/// </summary>
public sealed record RenameEdit(DocumentId DocumentId, TextSpan Span, string NewText);

/// <summary>
/// Result of semantic rename planning.
/// </summary>
public abstract record RenameResult;

/// <summary>
/// A complete, validated rename plan. An empty edit list represents a valid no-op rename.
/// </summary>
public sealed record RenameSuccess(IReadOnlyList<RenameEdit> Edits) : RenameResult;

/// <summary>
/// A rename request rejected by compiler/tooling semantics.
/// </summary>
public sealed record RenameFailure(string Message) : RenameResult;
