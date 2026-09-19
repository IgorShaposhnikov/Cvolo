namespace Cvolo.Compiler.Tooling;

/// <summary>
/// The semantic symbol resolved at a source position, together with its occurrence span and a
/// compiler-owned display string suitable for hover presentation.
/// </summary>
public sealed record SymbolLookupResult(
	SymbolId SymbolId,
	TextSpan SubjectSpan,
	ToolingSymbolKind Kind,
	string Name,
	string DisplayText,
	string? Documentation = null);
