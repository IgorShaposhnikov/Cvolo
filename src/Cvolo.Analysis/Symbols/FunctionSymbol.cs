using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Symbols;

/// <summary>
/// How a function callable is reached. This is the authoritative source of truth for
/// receiver-backedness; the synthetic parameter-0 named "this" is only an implementation detail
/// used for instance code generation.
/// </summary>
public enum CallableKind
{
	/// <summary>
	/// A plain function with no receiver (top-level, extern block, expose block, ...).
	/// </summary>
	Free,
	/// <summary>
	/// An extension block member written without a leading dot. Receives a synthetic 'this'
	/// parameter at index 0 and is called through a value: <c>value.Method()</c>.
	/// </summary>
	InstanceExtension,
	/// <summary>
	/// An extension block member written with a leading dot ('.Method'). Has no receiver at all
	/// and is called through its owner type: <c>Type.Method()</c>.
	/// </summary>
	Associated
}

public sealed class FunctionSymbol(
	string name,
	TypeSymbol returnType,
	IReadOnlyList<ParameterSymbol> parameters,
	bool isExtern = false,
	bool isVariadic = false) : Symbol(name)
{
	public TypeSymbol ReturnType { get; } = returnType;
	public IReadOnlyList<ParameterSymbol> Parameters { get; } = parameters;
	public bool IsExtern { get; } = isExtern;
	public bool IsVariadic { get; } = isVariadic;

	/// <summary>
	/// How this callable is reached. Defaults to <see cref="CallableKind.Free"/>; extension
	/// registration sets <see cref="CallableKind.InstanceExtension"/> or
	/// <see cref="CallableKind.Associated"/>.
	/// </summary>
	public CallableKind CallableKind { get; set; } = CallableKind.Free;

	/// <summary>
	/// True when the callable takes a receiver (instance extension method).
	/// </summary>
	public bool IsInstanceExtension => CallableKind == CallableKind.InstanceExtension;

	/// <summary>
	/// True when the callable is receiverless and called through its owner type.
	/// </summary>
	public bool IsAssociated => CallableKind == CallableKind.Associated;

	/// <summary>
	/// Intrinsic [UnsafeBody] marker - SafetyPass treats the body as unsafe (consumed by the unmanaged milestone).
	/// </summary>
	public bool IsUnsafeBody { get; set; }

	/// <summary>
	/// Intrinsic [NoAlias] marker - emitter attaches LLVM noalias to reference params (unbound/unsafe tiers).
	/// </summary>
	public bool IsNoAlias { get; set; }

	/// <summary>
	/// Target LLVM intrinsic name when decorated with [Intrinsic("llvm.name")].
	/// </summary>
	public string? IntrinsicName { get; set; }

	/// <summary>
	/// Warning ids suppressed via [SuppressWarning("id")] on this declaration.
	/// </summary>
	public List<string> SuppressedWarnings { get; } = [];

	/// <summary>
	/// Safety tier: Safe (default), Unbound, or Unsafe. Set from function modifier or [UnsafeBody] attribute.
	/// </summary>
	public SafetyTier SafetyTier { get; set; }
	/// <summary>
	/// Intrinsic [MustUse] marker — callers must not discard the returned value.
	/// </summary>
	public bool IsMustUse { get; set; }
	public string? MustUseMessage { get; set; }

	/// <summary>
	/// Intrinsic [Inline] marker - emitter attaches LLVM 'alwaysinline' to the function.
	/// </summary>
	public bool IsInline { get; set; }

	/// <summary>
	/// Intrinsic [NeverInline] marker - emitter attaches LLVM 'noinline' to the function.
	/// </summary>
	public bool IsNeverInline { get; set; }

	/// <summary>
	/// Override for the native symbol name used by the linker. Set by [ImportName] on extern block functions.
	/// When null, the function's own Name is used as the native symbol.
	/// </summary>
	public string? ImportName { get; set; }

	/// <summary>
	/// The library name from [LibraryImport] on the enclosing extern block, propagated to all functions inside.
	/// </summary>
	public string? LibraryName { get; set; }

	/// <summary>
	/// Platform-specific library path overrides from [LibraryImport] on the enclosing extern block.
	/// </summary>
	public string? WinPath { get; set; }
	public string? LinuxPath { get; set; }
	public string? MacPath { get; set; }

	/// <summary>
	/// Calling convention from the extern block ("C" or "system"). Null defaults to "C".
	/// </summary>
	public string? CallingConvention { get; set; }

	/// <summary>
	/// True when this function lives inside an `expose extern` block and receives a synthesized export alias.
	/// </summary>
	public bool IsExported { get; set; }

	/// <summary>
	/// True for functions declared with a native-ABI calling convention slot: 'unsafe "C"' / 'unsafe "system"'
	/// before the return type. The implementation is Cvolo, but the signature follows the native ABI and its
	/// address can be taken as a callback (see NativeDelegateTypeSymbol / &Function).
	/// </summary>
	public bool IsNativeAbi { get; set; }

	/// <summary>
	/// Native symbol name used by the export alias. Set by [ExposeName]; defaults to the source function name.
	/// </summary>
	public string? ExposeName { get; set; }
}

/// <summary>
/// An extension block member that declares its own type parameters, captured before parameter and
/// return types are resolved because those types may name the method's type parameters. A call site
/// substitutes concrete type arguments, then a <see cref="FunctionSymbol"/> is registered on the owner's
/// associated or instance overload table, mirroring how an ordinary generic function is instantiated.
/// </summary>
/// <param name="Method">The source declaration, still generic.</param>
/// <param name="OwnerType">The resolved extended type, used as the receiver type for instance forms.</param>
/// <param name="BaseMangledName">The <c>Namespace.Type.Member</c> key of the owning overload table.</param>
public sealed record GenericExtensionMethodTemplate(
	FunctionDeclarationSyntax Method,
	TypeSymbol OwnerType,
	string BaseMangledName);
