namespace Cvolo.Compiler.Tooling;

/// <summary>
/// How a type is related to a contract. Nominal interfaces are implemented; structural protocols
/// are conformed to. The distinction is carried to the editor rather than flattened, because the
/// two relations mean different things even when both are "implementations" in LSP terms.
/// </summary>
public enum ToolingImplementationKind
{
	Implements,
	ConformsTo,
}

/// <summary>
/// A concrete place that satisfies a contract: the conformance extension for a nominal interface,
/// or the declaration of a structurally conforming type. When the compiler cannot choose a single
/// satisfying member (a protocol member with several candidates), every candidate is returned and
/// the ambiguity is recorded rather than resolved here.
/// </summary>
public sealed record ToolingImplementation(
	ToolingImplementationKind Kind,
	SymbolDefinition Definition,
	string DisplayText,
	bool IsAmbiguous = false);
