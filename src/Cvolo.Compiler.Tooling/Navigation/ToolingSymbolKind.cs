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
	Delegate,
	TypeAlias,
	TypeParameter,
	Function,
	Method,
	ExtensionMethod,
	/// <summary>
	/// A receiverless extension member (declared with a leading dot) that is called through its
	/// owner type. Deliberately distinct from <see cref="ExtensionMethod"/> so editor features can
	/// keep the two callable forms apart.
	/// </summary>
	AssociatedFunction,
	Constructor,
	Destructor,
	Field,
	Parameter,
	Local,
	Global,
	Constant,
	Operator,
	OtherType,
	Keyword,
}
