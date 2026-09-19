namespace Cvolo.Compiler.Tooling;

/// <summary>
/// One node of a document's semantic declaration outline. Children are ordered in source order.
/// </summary>
public sealed record DocumentSymbolInfo(
	SymbolId SymbolId,
	string Name,
	string? Detail,
	ToolingSymbolKind Kind,
	TextSpan Range,
	TextSpan SelectionSpan,
	IReadOnlyList<DocumentSymbolInfo> Children);
