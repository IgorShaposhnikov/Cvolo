namespace Cvolo.Compiler.Tooling;

/// <summary>
/// One contract in a declared type hierarchy. A hierarchy is contract inheritance only: an
/// interface may inherit another interface or a protocol, and a protocol may inherit another
/// protocol. A concrete type that structurally conforms to a protocol is never a hierarchy
/// member, so the tree never mixes declared inheritance with structural conformance.
/// </summary>
public sealed record ToolingHierarchyItem(
	SymbolId SymbolId,
	ToolingSymbolKind Kind,
	string Name,
	SymbolDefinition Definition);
