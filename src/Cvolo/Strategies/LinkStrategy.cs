using System.Diagnostics;
using Cvolo.Core.Diagnostics;
using Cvolo.Packaging;
using Cvolo.Projects;

namespace Cvolo.Strategies;

/// <summary>
/// A native link input lowered by the driver into exactly one of two shapes: an explicit file path
/// (application mapping or legacy source-level OS path) or a bare <c>-l</c> system name.
/// <paramref name="Origin"/> records which package (or "&lt;application&gt;") required the library so
/// CVL1704 diagnostics can point at the owner.
/// </summary>
/// <param name="LibraryName">The logical [LibraryImport] name (ordinal, case-sensitive).</param>
/// <param name="FullPath">Explicit link input path; null when <paramref name="SystemName"/> is set.</param>
/// <param name="SystemName">Bare system library name passed as <c>-l&lt;name&gt;</c>; null when <paramref name="FullPath"/> is set.</param>
/// <param name="Origin">The originating package id or "&lt;application&gt;".</param>
public sealed record NativeLinkInput(string LibraryName, string? FullPath, string? SystemName, string Origin)
{
	internal const string ApplicationOrigin = "<application>";
}

internal sealed class LinkStrategy(string binDirectory) : ICompilationStrategy
{
	public int Execute(string llPath, CompilationProject project, string? linkerPath, string? linkerName, string optLevel = "Os", bool verbose = false, IEnumerable<NativeLinkInput>? nativeLibraries = null, string? targetOs = null, string? nativeTargetLabel = null, IEnumerable<string>? additionalInputs = null, DebugLinkOptions? debug = null)
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

		// Debug symbols are a link-time product: the module only carries the CodeView or DWARF
		// metadata, and the linker decides where the resulting program database or .debug_* sections
		// land. Without this switch a Windows link silently produces no .pdb at all.
		var debugFlag = "";
		if (debug is { Enabled: true })
		{
			if (!isClang)
			{
				Console.Error.WriteLine(
					$"error {DiagnosticIds.DebugLinkerUnsupported}: linker for triple '{debug.Triple ?? "unknown"}' has no debug-symbol support; " +
					"select the bundled clang or a system clang to use --debug.");
				return 1;
			}

			// PE targets need MSVC's /DEBUG (via the -Xlinker pass-through) plus an explicit program
			// database path; ELF and Mach-O only need -g.
			debugFlag = debug.IsWindowsMsvcTarget
				? $" -g -Xlinker /DEBUG -Xlinker /PDB:\"{Path.GetFullPath(Path.Combine(binDirectory, project.OutputName + ".pdb"))}\""
				: " -g";
		}

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

		// FFI native libraries: the driver already lowered each logical [LibraryImport] requirement
		// into an explicit file path (application mapping or legacy source-level OS path) or a bare
		// -l system name. Paths are forwarded verbatim — no host-disk existence check, so cross
		// compilation is respected and a missing selected input surfaces as a CVL1704 from the linker.
		var libraryFlags = "";
		if (nativeLibraries is not null)
		{
			foreach (var lib in nativeLibraries)
			{
				if (!string.IsNullOrEmpty(lib.FullPath))
				{
					libraryFlags += $" \"{lib.FullPath}\"";
				}
				else if (!string.IsNullOrEmpty(lib.SystemName))
				{
					libraryFlags += $" -l{lib.SystemName}";
				}
				else
				{
					libraryFlags += $" -l{lib.LibraryName}";
				}
			}
		}

		if (verbose)
		{
			Console.WriteLine($"{AnsiConsoleStyle.Header("Linking using:")} {AnsiConsoleStyle.Value(linkerName ?? "<unknown>")}...");
		}

		// Always capture linker output, including verbose builds. Tests and IDE callers
		// redirect Console.Out/Error, but cannot capture a child process that inherits the
		// terminal directly. Keeping both pipes redirected also avoids losing the actual
		// LLVM/linker diagnostic when a package bitcode link fails.
		var psi = new ProcessStartInfo
		{
			FileName = linkerPath,
			Arguments = $"-o \"{binaryPath}\" \"{llPath}\"{packageInputFlags}{typeFlag}{visibilityFlag}{optFlag}{libraryFlags}{subsystemFlag}{debugFlag}",
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
			// Mach-O keeps debug information in an external bundle that the link itself does not
			// produce; dsymutil must run before the binary can be symbolicated.
			if (debug is { ExpectsDsymBundle: true } && !CreateDsymBundle(binaryPath, verbose))
			{
				Console.Error.WriteLine(
					$"error {DiagnosticIds.DebugSymbolBundleMissing}: dsymutil did not produce '{BuildOutputLayout.GetNativeDsymPath(binaryPath)}' for the debug build.");
				return 1;
			}

			if (verbose)
			{
				if (!string.IsNullOrWhiteSpace(linkerStdout))
					Console.Write(linkerStdout);
				if (!string.IsNullOrWhiteSpace(linkerStderr))
					Console.Error.Write(linkerStderr);
				Console.WriteLine($"{AnsiConsoleStyle.Success("Built:")} {AnsiConsoleStyle.Path(binaryPath)}");
			}

			return 0;
		}

		var inputs = nativeLibraries?.ToList() ?? [];
		if (inputs.Count > 0 && LooksLikeMissingNativeLibrary(linkerStderr) && MissingNativeAttributedToInput(linkerStderr, inputs))
		{
			// Recognized missing-native failure: raw linker output is hidden by default and only
			// shown before the Cvolo diagnostic when --verbose is requested.
			if (verbose)
			{
				if (!string.IsNullOrWhiteSpace(linkerStdout))
					Console.Error.Write(linkerStdout);
				Console.Error.Write(linkerStderr);
			}

			var targetLabel = nativeTargetLabel ?? ResolveTargetOs(targetOs);
			var os = targetLabel.Split('/')[0];
			foreach (var lib in inputs)
			{
				ReportNativeLibraryUnresolved(lib, targetLabel, os);
			}

			return 1;
		}

		Console.Error.WriteLine("Linking failed:");
		if (!string.IsNullOrWhiteSpace(linkerStdout))
			Console.Error.Write(linkerStdout);
		Console.Error.WriteLine(linkerStderr);

		return 1;
	}

	/// <summary>
	/// Reports a recognized missing-native failure as CVL1704, shaped by whether an explicit path
	/// was configured (show the path + its application-root resolution) or only a bare system name
	/// was attempted (recommend adding a mapping).
	/// </summary>
	private static void ReportNativeLibraryUnresolved(NativeLinkInput lib, string targetLabel, string os)
	{
		var originLabel = lib.Origin == NativeLinkInput.ApplicationOrigin
			? "The application"
			: $"Package '{lib.Origin}'";

		if (!string.IsNullOrEmpty(lib.FullPath))
		{
			var relative = TryMakeRelative(lib.FullPath);
			Console.Error.WriteLine(
				$"Compile Error {DiagnosticIds.NativeLibraryUnresolved}: {originLabel} requires native library '{lib.LibraryName}', " +
				$"but it could not be linked using the configured path '{lib.FullPath}' (from the application root: '{relative}') for target '{targetLabel}'. " +
				"Verify the file exists and can be linked for this target.");
		}
		else
		{
			Console.Error.WriteLine(
				$"Compile Error {DiagnosticIds.NativeLibraryUnresolved}: {originLabel} requires native library '{lib.LibraryName}', " +
				$"but it could not be resolved for target '{targetLabel}'. " +
				$"Add a mapping to the application .cvlproj: <NativeLibraries Target=\"{os}\"><NativeLibrary For=\"{lib.LibraryName}\" Path=\"...\">" +
				"<RuntimeFile Path=\"...\" /></NativeLibrary></NativeLibraries>. RuntimeFile is optional.");
		}
	}

	private static string TryMakeRelative(string fullPath)
	{
		try
		{
			return Path.GetFileName(fullPath);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return fullPath;
		}
	}

	/// <summary>
	/// Confirms the missing-library signature actually references one of the native inputs (its path
	/// basename, system name, or logical name) so unrelated failures — a missing IR file, an object
	/// missing its own sibling, a plain link error — are never misclassified as missing native libs.
	/// </summary>
	private static bool MissingNativeAttributedToInput(string linkerStderr, IReadOnlyList<NativeLinkInput> inputs)
	{
		foreach (var lib in inputs)
		{
			if (lib.FullPath is not null
				&& linkerStderr.Contains(Path.GetFileName(lib.FullPath), StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			if (lib.SystemName is not null
				&& linkerStderr.Contains(lib.SystemName, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			if (lib.FullPath is null && lib.SystemName is null
				&& linkerStderr.Contains(lib.LibraryName, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Runs <c>dsymutil</c> over a freshly linked Mach-O binary and reports whether the expected
	/// <c>.dSYM</c> bundle now exists. A missing tool is treated the same as a missing bundle: either
	/// way the debug build cannot be symbolicated.
	/// </summary>
	private static bool CreateDsymBundle(string binaryPath, bool verbose)
	{
		var dsymutilPath = FindOnPath("dsymutil");
		if (dsymutilPath is null)
		{
			Console.Error.WriteLine("error: dsymutil was not found on PATH; a Mach-O debug build cannot emit a .dSYM bundle.");
			return false;
		}

		if (verbose)
			Console.WriteLine($"Creating debug symbol bundle for {binaryPath}...");

		using var bundle = Process.Start(new ProcessStartInfo
		{
			FileName = dsymutilPath,
			Arguments = $"\"{binaryPath}\"",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = !verbose
		});

		if (bundle is null)
			return false;

		bundle.WaitForExit(60_000);
		if (verbose && !bundle.HasExited)
		{
			try { bundle.Kill(entireProcessTree: true); } catch { }
		}

		return Directory.Exists(BuildOutputLayout.GetNativeDsymPath(binaryPath));
	}

	private static string? FindOnPath(string toolName)
	{
		var pathVariable = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(pathVariable))
			return null;

		var extensions = OperatingSystem.IsWindows()
			? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
			: [string.Empty];

		foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			foreach (var extension in extensions)
			{
				var candidate = Path.Combine(directory.Trim(), toolName + extension);
				if (File.Exists(candidate))
					return candidate;
			}
		}

		return null;
	}

	private static bool LooksLikeMissingNativeLibrary(string stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
			return false;

		// MSVC link.exe / lld-link reports missing -l inputs as LNK1181 rather
		// than with the Unix "library not found" wording. Attribution to a
		// requested native input is still required before emitting CVL1704.
		return (stderr.Contains("LNK1181", StringComparison.OrdinalIgnoreCase)
			&& stderr.Contains("cannot open input file", StringComparison.OrdinalIgnoreCase))
			|| stderr.Contains("unable to find library", StringComparison.OrdinalIgnoreCase)
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
