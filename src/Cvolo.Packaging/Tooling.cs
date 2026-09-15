using System.Diagnostics;

namespace Cvolo.Packaging;

/// <summary>
/// Locates a clang (or gcc/g++) driver for compiling the emitted .ll into object code
/// and bitcode. Prefers a bundled compiler next to the compiler host; falls back to PATH.
/// </summary>
internal static class ClangTool
{
	private static readonly string[] _candidates = ["clang", "gcc", "g++"];

	public static string? ResolvePath()
	{
		var localName = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
		var bundled = Path.Combine(AppContext.BaseDirectory, localName);
		if (File.Exists(bundled))
			return bundled;

		foreach (var candidate in _candidates)
		{
			var found = FindOnPath(candidate);
			if (found is not null)
				return found;
		}

		return null;
	}

	private static string? FindOnPath(string executable)
	{
		var pathEnv = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(pathEnv))
			return null;

		var withExtension = OperatingSystem.IsWindows() ? executable + ".exe" : executable;
		foreach (var dir in pathEnv.Split(Path.PathSeparator))
		{
			if (string.IsNullOrEmpty(dir))
				continue;

			try
			{
				var candidate = Path.Combine(dir, withExtension);
				if (File.Exists(candidate))
					return candidate;
			}
			catch (Exception ex) when (ex is ArgumentException or PathTooLongException or UnauthorizedAccessException)
			{
				// Skip unreadable PATH entries.
			}
		}

		return null;
	}
}

/// <summary>
/// Runs an external tool with captured output, throwing on a nonzero exit code.
/// </summary>
internal static class ProcessRunner
{
	public static void Run(string toolPath, string toolName, params string[] args)
	{
		var psi = new ProcessStartInfo
		{
			FileName = toolPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		foreach (var arg in args)
			psi.ArgumentList.Add(arg);

		using var process = Process.Start(psi);
		if (process is null)
			throw new InvalidOperationException($"Failed to start '{toolName}'.");

		var stdout = process.StandardOutput.ReadToEnd();
		var stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();

		if (process.ExitCode != 0)
			throw new InvalidOperationException(
				$"'{toolName}' failed (exit code {process.ExitCode}).\n{stderr.Trim()}\n{stdout.Trim()}");
	}
}
