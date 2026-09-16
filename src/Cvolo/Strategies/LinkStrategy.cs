using System.Diagnostics;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Packaging;

namespace Cvolo.Strategies;

internal sealed class LinkStrategy(string binDirectory) : ICompilationStrategy
{
	public int Execute(string llPath, CompilationProject project, string? linkerPath, string? linkerName, string optLevel = "Os", bool verbose = false, IEnumerable<NativeLibraryInfo>? nativeLibraries = null, string? targetOs = null, IEnumerable<string>? additionalInputs = null)
	{
		if (linkerPath is null)
		{
			Console.Error.WriteLine("Error: no compatible linker found (bundled clang, system clang, gcc, or g++). Install LLVM or GCC tools to compile.");
			return 1;
		}

		var binaryExt = project.IsShared
			? (OperatingSystem.IsWindows() ? ".dll" : ".so")
			: (OperatingSystem.IsWindows() ? ".exe" : "");
		var binaryPath = Path.Combine(binDirectory, project.OutputName + binaryExt);

		var typeFlag = project.IsShared ? " -shared" : "";
		var visibilityFlag = project.IsShared
			? (OperatingSystem.IsWindows() ? " -fvisibility=hidden" : " -fvisibility=hidden -fPIC")
			: "";

		// The IR is already optimized in-process via IrOptimizer; passing the same
		// -O level to the backend linker keeps instruction selection, scheduling,
		// and register allocation at matching aggressiveness instead of clang's
		// default -O0 codegen.
		var optFlag = $" -O{optLevel.TrimStart('-')[1..]}";

		// Subsystem flag is needed when using Clang (bundled or system) on Windows
		var isClang = linkerName == "bundled-clang" || linkerName == "clang";
		var subsystemFlag = isClang && OperatingSystem.IsWindows() && !project.IsShared
			? " -Xlinker /subsystem:console"
			: "";

		// Local Package Manager Phase 4 links the installed package's Sector 3 LLVM
		// bitcode directly with the application's IR. Preserve deterministic id@version order.
		var packageInputs = additionalInputs?
			.Select(Path.GetFullPath)
			.ToArray() ?? [];
		if (packageInputs.Any(path => string.Equals(Path.GetExtension(path), ".bc", StringComparison.OrdinalIgnoreCase)) && !isClang)
		{
			Console.Error.WriteLine($"error {PackageDiagnosticIds.BitcodeLinkerUnavailable}: Package Sector 3 bitcode requires clang/LLVM; selected linker is '{linkerName}'.");
			return 1;
		}

		var packageInputFlags = string.Concat(packageInputs.Select(path => $" \"{path}\""));

		// FFI native libraries: [LibraryImport] forwards ONLY the path matching the current
		// compilation target OS (win:/linux:/mac:); a missing target path falls back to the
		// bare library name via -l<name>. Path strings are forwarded verbatim (resolved
		// relative to the project directory) — no host-disk existence check, so cross
		// compilation is respected and the static linker reports CVL1704 if a file is gone.
		var effectiveOs = ResolveTargetOs(targetOs);
		var libraryFlags = "";
		if (nativeLibraries is not null)
		{
			foreach (var lib in nativeLibraries)
			{
				var targetPath = effectiveOs switch
				{
					"windows" => lib.WinPath,
					"linux" => lib.LinuxPath,
					"macos" => lib.MacPath,
					_ => null
				};

				if (!string.IsNullOrEmpty(targetPath))
				{
					// System marker: If the path does not contain directory separators, treat it as a 
					// system library name from the Windows SDK / Linux library search paths (e.g., "opengl32.lib").
					if (!targetPath.Contains('/') && !targetPath.Contains('\\'))
					{
						// Strip the file extension (e.g., "opengl32.lib" -> "opengl32") to pass cleanly to Clang
						var cleanLibName = Path.GetFileNameWithoutExtension(targetPath);
						libraryFlags += $" -l{cleanLibName}";
					}
					else
					{
						// If separators are present, it is a local relative asset path; resolve it 
						// deterministically relative to the project directory root.
						libraryFlags += $" \"{Path.GetFullPath(targetPath, project.ProjectDirectory)}\"";
					}
				}
				else
				{
					// Core fallback: Use the base library primitive identifier name if target parameters are absent
					libraryFlags += $" -l{lib.LibraryName}";
				}
			}
		}

		if (verbose)
		{
			Console.WriteLine($"Linking using: {linkerName}...");
		}

		// Always capture linker output, including verbose builds. Tests and IDE callers
		// redirect Console.Out/Error, but cannot capture a child process that inherits the
		// terminal directly. Keeping both pipes redirected also avoids losing the actual
		// LLVM/linker diagnostic when a package bitcode link fails.
		var psi = new ProcessStartInfo
		{
			FileName = linkerPath,
			Arguments = $"-o \"{binaryPath}\" \"{llPath}\"{packageInputFlags}{typeFlag}{visibilityFlag}{optFlag}{libraryFlags}{subsystemFlag}",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = !verbose
		};

		using var linkResult = Process.Start(psi);
		if (linkResult is null)
		{
			Console.Error.WriteLine($"Failed to start linker '{linkerName}'.");
			return 1;
		}

		// Drain both redirected streams concurrently. Waiting before reading stderr can
		// deadlock if clang fills its pipe while reporting a large LLVM/linker error.
		var stdoutTask = linkResult.StandardOutput.ReadToEndAsync();
		var stderrTask = linkResult.StandardError.ReadToEndAsync();

		const int linkerTimeoutMilliseconds = 60_000;
		if (!linkResult.WaitForExit(linkerTimeoutMilliseconds))
		{
			try { linkResult.Kill(entireProcessTree: true); } catch { }
			Console.Error.WriteLine($"Linking timed out after {linkerTimeoutMilliseconds / 1000} seconds.");
			return 1;
		}

		Task.WaitAll(stdoutTask, stderrTask);
		var linkerStdout = stdoutTask.Result;
		var linkerStderr = stderrTask.Result;

		if (linkResult.ExitCode == 0)
		{
			if (verbose)
			{
				if (!string.IsNullOrWhiteSpace(linkerStdout))
					Console.Write(linkerStdout);
				if (!string.IsNullOrWhiteSpace(linkerStderr))
					Console.Error.Write(linkerStderr);
				Console.WriteLine($"Built: {binaryPath}");
			}

			return 0;
		}

		if (nativeLibraries is not null && nativeLibraries.Any() && LooksLikeMissingNativeLibrary(linkerStderr))
		{
			Console.Error.WriteLine($"Compile Error {DiagnosticIds.NativeLibraryUnresolved}: A native library requested via [LibraryImport] could not be resolved by the linker.");
		}
		Console.Error.WriteLine("Linking failed:");
		if (!string.IsNullOrWhiteSpace(linkerStdout))
			Console.Error.Write(linkerStdout);
		Console.Error.WriteLine(linkerStderr);

		return 1;
	}

	private static bool LooksLikeMissingNativeLibrary(string stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
			return false;

		return stderr.Contains("unable to find library", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("library not found", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("cannot find -l", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("could not open", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("cannot open file", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase);
	}

	private static string ResolveTargetOs(string? targetOs)
	{
		if (string.IsNullOrWhiteSpace(targetOs) || targetOs.Equals("host", StringComparison.OrdinalIgnoreCase))
		{
			if (OperatingSystem.IsWindows()) return "windows";
			if (OperatingSystem.IsLinux()) return "linux";
			if (OperatingSystem.IsMacOS()) return "macos";
			return "windows";
		}

		return targetOs.ToLowerInvariant() switch
		{
			"windows" or "win" or "win32" => "windows",
			"linux" => "linux",
			"macos" or "mac" or "osx" or "darwin" => "macos",
			_ => "windows"
		};
	}
}