using System.Diagnostics;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;

namespace Cvolo.Strategies;

internal sealed class LinkStrategy(string binDirectory) : ICompilationStrategy
{
	public int Execute(string llPath, CompilationProject project, string? linkerPath, string? linkerName, string optLevel = "Os", bool verbose = false, IEnumerable<NativeLibraryInfo>? nativeLibraries = null, string? targetOs = null)
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

		// Configure ProcessStartInfo to redirect and suppress standard output and standard error if not in verbose mode
		var psi = new ProcessStartInfo
		{
			FileName = linkerPath,
			Arguments = $"-o \"{binaryPath}\" \"{llPath}\"{typeFlag}{visibilityFlag}{optFlag}{libraryFlags}{subsystemFlag}",
			RedirectStandardOutput = !verbose,
			RedirectStandardError = !verbose,
			UseShellExecute = false,
			CreateNoWindow = !verbose
		};

		using var linkResult = Process.Start(psi);
		linkResult?.WaitForExit();

		if (linkResult?.ExitCode == 0)
		{
			if (verbose)
			{
				Console.WriteLine($"Built: {binaryPath}");
			}

			return 0;
		}

		// Fallback: If linking failed, print Clang's actual errors even in silent mode so the developer knows what went wrong!
		if (!verbose && linkResult is not null)
		{
			var errors = linkResult.StandardError.ReadToEnd();
			if (nativeLibraries is not null && nativeLibraries.Any())
			{
				Console.Error.WriteLine($"Compile Error {DiagnosticIds.NativeLibraryUnresolved}: A native library requested via [LibraryImport] could not be resolved by the linker.");
			}
			Console.Error.WriteLine("Linking failed:");
			Console.Error.WriteLine(errors);
		}

		return 1;
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
