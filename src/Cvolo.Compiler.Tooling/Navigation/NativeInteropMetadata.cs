namespace Cvolo.Compiler.Tooling;

/// <summary>
/// The native-interop shape attached to a symbol surfaced by tooling. The compiler remains the
/// source of truth; this DTO only exposes metadata that is already present on the bound package or
/// source declaration so LSP clients do not need to parse attributes themselves.
/// </summary>
public enum NativeInteropKind
{
	None,
	NativeDelegate,
	RawUnion,
	ForeignGlobal,
}

/// <summary>
/// Structured native-interop metadata reconstructed from the declaration represented by a hover or
/// navigation result. Platform-specific library paths are preserved verbatim for cross-target tools.
/// </summary>
public sealed record NativeInteropMetadata(
	NativeInteropKind Kind,
	string? CallingConvention = null,
	string? ImportName = null,
	string? LibraryName = null,
	string? WinPath = null,
	string? LinuxPath = null,
	string? MacPath = null);
