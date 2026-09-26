namespace Cvolo.Analysis.Builtins;

public enum BuiltinId
{
	Unknown = 0,
	SizeOf,
	AlignOf,
	OffsetOf,
	Type,
	UnsafeBodyAttribute,
	NoAliasAttribute,
	SuppressWarningAttribute,
	FlagsAttribute,
	NonExhaustiveAttribute,
	StrictMutabilityAttribute,
	IntrinsicAttribute,
	MustUseAttribute,
	ErrorAttribute,
	ResultAttribute,
	InlineAttribute,
	NeverInlineAttribute,
	LibraryImportAttribute,
	ImportNameAttribute,
	ExposeNameAttribute
}

public enum BuiltinImplementationPolicy
{
	CompileTimeOnly,
	ConstantFolded,
	CompilerLowered,
	RuntimeType,
	BackendIntrinsic
}

public enum BuiltinDeclarationKind
{
	Function,
	Struct,
	Union,
	Enum
}

public sealed record BuiltinEntry(
	BuiltinId Id,
	string Name,
	BuiltinDeclarationKind Kind,
	BuiltinImplementationPolicy Policy);

public static class BuiltinCatalog
{
	private static readonly Dictionary<string, BuiltinEntry> Entries = new(StringComparer.Ordinal)
	{
		["sizeof"] = new(BuiltinId.SizeOf, "sizeof", BuiltinDeclarationKind.Function, BuiltinImplementationPolicy.ConstantFolded),
		["alignof"] = new(BuiltinId.AlignOf, "alignof", BuiltinDeclarationKind.Function, BuiltinImplementationPolicy.ConstantFolded),
		["offsetof"] = new(BuiltinId.OffsetOf, "offsetof", BuiltinDeclarationKind.Function, BuiltinImplementationPolicy.ConstantFolded),
		["Type"] = new(BuiltinId.Type, "Type", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.RuntimeType),
		["UnsafeBodyAttribute"] = new(BuiltinId.UnsafeBodyAttribute, "UnsafeBodyAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["NoAliasAttribute"] = new(BuiltinId.NoAliasAttribute, "NoAliasAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["SuppressWarningAttribute"] = new(BuiltinId.SuppressWarningAttribute, "SuppressWarningAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["FlagsAttribute"] = new(BuiltinId.FlagsAttribute, "FlagsAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["NonExhaustiveAttribute"] = new(BuiltinId.NonExhaustiveAttribute, "NonExhaustiveAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["StrictMutabilityAttribute"] = new(BuiltinId.StrictMutabilityAttribute, "StrictMutabilityAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["IntrinsicAttribute"] = new(BuiltinId.IntrinsicAttribute, "IntrinsicAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["MustUseAttribute"] = new(BuiltinId.MustUseAttribute, "MustUseAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["ErrorAttribute"] = new(BuiltinId.ErrorAttribute, "ErrorAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["ResultAttribute"] = new(BuiltinId.ResultAttribute, "ResultAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["InlineAttribute"] = new(BuiltinId.InlineAttribute, "InlineAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["NeverInlineAttribute"] = new(BuiltinId.NeverInlineAttribute, "NeverInlineAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["LibraryImportAttribute"] = new(BuiltinId.LibraryImportAttribute, "LibraryImportAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["ImportNameAttribute"] = new(BuiltinId.ImportNameAttribute, "ImportNameAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly),
		["ExposeNameAttribute"] = new(BuiltinId.ExposeNameAttribute, "ExposeNameAttribute", BuiltinDeclarationKind.Struct, BuiltinImplementationPolicy.CompileTimeOnly)
	};

	public static bool TryGet(string name, out BuiltinEntry entry) => Entries.TryGetValue(name, out entry!);

	public static IReadOnlyCollection<BuiltinEntry> All => Entries.Values;
}
