namespace Cvolo.Compiler.Tooling;

/// <summary>
/// One source declaration of a symbol: the document it lives in, the full declaration range, and
/// the name selection span used for editor navigation.
/// </summary>
public sealed record SymbolDefinition(DocumentId DocumentId, TextSpan Range, TextSpan SelectionSpan);
