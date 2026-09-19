namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Compiler-owned classification of a symbol surfaced by the navigation API. Independent of any
/// editor protocol symbol kind.
/// </summary>
public enum ToolingSymbolKind
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
