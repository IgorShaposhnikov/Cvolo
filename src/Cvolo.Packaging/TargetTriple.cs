using System.Runtime.InteropServices;

namespace Cvolo.Packaging;

/// <summary>
/// Maps portable target names (win-x64, linux-arm64, ...) to canonical LLVM triples
/// and resolves the build host's own triple. Full LLVM triples pass through unchanged.
/// </summary>
public static class TargetTriple
{
	private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
	{
		["win-x64"] = "x86_64-pc-windows-msvc",
		["linux-x64"] = "x86_64-pc-linux-gnu",
		["linux-arm64"] = "aarch64-pc-linux-gnu",
		["osx-x64"] = "x86_64-apple-darwin",
		["osx-arm64"] = "aarch64-apple-darwin"
	};

	/// <summary>
	/// Resolves a portable or full triple to a canonical LLVM triple.
	/// Unknown values pass through unchanged (treated as full triples).
	/// </summary>
	public static string Resolve(string target)
	{
		var trimmed = target.Trim();
		return Known.TryGetValue(trimmed, out var triple) ? triple : trimmed;
	}

	/// <summary>
	/// The canonical LLVM triple of the machine this process runs on.
	/// </summary>
	public static string HostTriple()
	{
		var arch = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => "x86"
		};

		var os = OperatingSystem.IsWindows() ? "win"
			: OperatingSystem.IsLinux() ? "linux"
			: "osx";

		return Resolve($"{os}-{arch}");
	}
}
