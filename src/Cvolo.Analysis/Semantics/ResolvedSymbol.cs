using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Compiler-owned classification of a resolved symbol. Internal to the language-server backend
/// boundary; it is deliberately independent of any LSP symbol kind.
/// </summary>
public enum ResolvedSymbolKind
{
	Unknown,
	Namespace,
	Module,
	Struct,
	Union,
	Enum,
	EnumMember,
	Interface,
	Protocol,
	Delegate,
	TypeAlias,
	TypeParameter,
	Function,
	Method,
	ExtensionMethod,
	Constructor,
	Destructor,
	Field,
	Parameter,
	Local,
	Global,
	Constant,
	Operator,
	OtherType,
}

/// <summary>
/// The semantic symbol bound at a source position: its classification, the declaration node that
/// defines it, the subject span of the occurrence, and a compiler-owned display string.
/// </summary>
public sealed record ResolvedSymbol(
	ResolvedSymbolKind Kind,
	string Name,
	string? OwnerTypeName,
	TextSpan SubjectSpan,
	SyntaxNode Declaration,
	string DisplayText,
	string? Documentation = null);
