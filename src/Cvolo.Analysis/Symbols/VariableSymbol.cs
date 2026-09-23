using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols;

public sealed class VariableSymbol(string name, TypeSymbol type, bool isMutable) : Symbol(name)
{
	public TypeSymbol Type { get; } = type;
	public bool IsMutable { get; } = isMutable;
	public bool IsMoved { get; set; } = false;
	public OriginKind Origin { get; set; } = OriginKind.Local;
	public bool IsHeapAllocated { get; set; } = false;
	public bool IsInitialized { get; set; } = false;
	public bool IsGlobal { get; set; } = false;
	public bool IsRawPointer { get; set; } = false;

	/// <summary>The namespace that declared this global; null for the global (root) namespace.</summary>
	public string? DeclaringNamespace { get; set; }

	/// <summary>Fully-qualified name ("Ns.Sub.Name", or just "Name" for the root namespace).</summary>
	public string QualifiedGlobalName =>
		string.IsNullOrEmpty(DeclaringNamespace) ? Name : $"{DeclaringNamespace}.{Name}";

	/// <summary>
	/// True for imported foreign globals (from an extern block or standalone 'extern "C" global ...').
	/// Such globals reference external mutable data: the source treats them as read-only but they are
	/// NOT LLVM constants (no initializer is emitted; the symbol resolves at link time).
	/// </summary>
	public bool IsForeign { get; set; }

	/// <summary>
	/// Override for the native symbol name of a foreign global. Set by [ImportName]; when null the
	/// global's own Name is used as the native symbol.
	/// </summary>
	public string? ImportName { get; set; }

	/// <summary>
	/// Calling convention of the extern block or standalone foreign-global declaration ("C" or "system").
	/// Null for ordinary Cvolo globals.
	/// </summary>
	public string? CallingConvention { get; set; }

	/// <summary>Native library the foreign global binds to, from the enclosing extern block's [LibraryImport].</summary>
	public string? LibraryName { get; set; }

	/// <summary>Optional explicit Windows library path for this foreign global's native library.</summary>
	public string? WinPath { get; set; }

	/// <summary>Optional explicit Linux library path for this foreign global's native library.</summary>
	public string? LinuxPath { get; set; }

	/// <summary>Optional explicit macOS library path for this foreign global's native library.</summary>
	public string? MacPath { get; set; }
}
