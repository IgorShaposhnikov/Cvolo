namespace Cvolo.Analysis.Symbols.FFI;

/// <summary>
/// Metadata for a native library requested via [LibraryImport] on an extern block.
/// <see cref="LibraryName"/> is the bare identifier (e.g. "glfw3" or "kernel32"); the linker
/// appends the platform-appropriate extension unless an explicit <paramref name="WinPath"/> /
/// <paramref name="LinuxPath"/> / <paramref name="MacPath"/> overrides it for the current OS.
/// </summary>
public sealed record NativeLibraryInfo(string LibraryName, string? WinPath, string? LinuxPath, string? MacPath);
