using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cvolo.Benchmarks;

/// <summary>
/// Locates the Cvolo compiler and the Rust toolchain, builds scenario binaries
/// once and caches them, and executes a binary capturing stdout.
/// </summary>
internal static class Toolchains
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> Executables = new();
    private static string? _root;

    /// <summary>
    /// The directory that contains the <c>Scenarios</c> folder. BenchmarkDotNet runs
    /// the benchmarks from a generated temp project, so <c>AppContext.BaseDirectory</c>
    /// does not point at our output; resolve from the benchmark assembly location
    /// (which stays in our output folder) instead.
    /// </summary>
    public static string Root
    {
        get
        {
            if (_root is not null)
				return _root;

            foreach (var start in new[]
                     {
                         Path.GetDirectoryName(typeof(BenchmarkSuite).Assembly.Location),
                         AppContext.BaseDirectory,
                     })
            {
                var dir = new DirectoryInfo(start!);
                while (dir is not null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "Scenarios")))
                        return _root = dir.FullName;
                    dir = dir.Parent;
                }
            }

            throw new InvalidOperationException(
                "Scenarios folder not found next to the benchmark assembly or application base directory.");
        }
    }

    public static string LocateCvolo()
    {
        var env = Environment.GetEnvironmentVariable("CVOLO_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
			return env;

        var dir = new DirectoryInfo(Root);
        while (dir is not null)
        {
            var srcDir = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(srcDir, "Cvolo")))
                foreach (var cfg in new[] { "Debug", "Release" })
                {
                    var candidate = Path.Combine(srcDir, "Cvolo", "bin", cfg, "net10.0", "Cvolo.exe");
                    if (File.Exists(candidate))
						return candidate;
                }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Cvolo.exe not found. Build src/Cvolo.slnx first, or set the CVOLO_EXE environment variable.");
    }

	/// <summary>
	/// Builds (cached) the Cvolo binary for a scenario at an optimization level,
	/// optionally without !tbaa tags, and counts the tags emitted in the -O0 IR once.
	/// </summary>
	public static string BuildCvolo(string baseDir, Scenario s, string opt, bool noTbaa)
	{
		var key = $"cvolo:{s.Name}:{opt}:{(noTbaa ? "notbaa" : "tbaa")}";

		var buildId = $"{s.Name}_{opt}_{(noTbaa ? "notbaa" : "tbaa")}";
		var buildDir = Path.Combine(ScenarioPaths.Directory(baseDir, s), "build_artifacts", buildId);
		Directory.CreateDirectory(buildDir);

		if (Executables.TryGetValue(key, out var cached))
			return cached;

		var cwd = ScenarioPaths.Directory(baseDir, s);
		var exe = Path.Combine(cwd, "bin", "Debug", s.Name + ".exe");
		var destination = Path.Combine(buildDir, s.Name + ".exe");

		var psi = Proc(LocateCvolo(), cwd);
		psi.ArgumentList.Add("build");
		psi.ArgumentList.Add(ScenarioPaths.Cvl(baseDir, s));
		psi.ArgumentList.Add("-O");
		psi.ArgumentList.Add(opt);
		if (noTbaa) psi.ArgumentList.Add("--no-tbaa");

		var (code, _, err) = Run(psi);
		if (code != 0)
			throw new InvalidOperationException($"Cvolo build failed:\n{err}");

		// Move the file from default location to unique build folder
		if (File.Exists(destination)) File.Delete(destination);
		File.Move(exe, destination);

		Executables[key] = destination;
		return destination;
	}

	public static string BuildRust(string baseDir, Scenario s)
	{
		lock (Sync)
		{
			var key = $"rust:{s.Name}";
			if (Executables.TryGetValue(key, out var cached))
				return cached;

			var rustBin = Path.Combine(baseDir, "rust-bin");
			Directory.CreateDirectory(rustBin);
			var exe = Path.Combine(rustBin, s.Name + ".exe");
			var psi = new ProcessStartInfo
			{
				FileName = "rustc",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			psi.ArgumentList.Add("-C");
			psi.ArgumentList.Add("opt-level=3");
			psi.ArgumentList.Add("-C");
			psi.ArgumentList.Add("lto=fat");
			psi.ArgumentList.Add("-C");
			psi.ArgumentList.Add("codegen-units=1");
			psi.ArgumentList.Add("--edition");
			psi.ArgumentList.Add("2021");
			psi.ArgumentList.Add(ScenarioPaths.Rust(baseDir, s));
			psi.ArgumentList.Add("-o");
			psi.ArgumentList.Add(exe);
			var (code, _, err) = Run(psi);
			if (code != 0)
				throw new InvalidOperationException($"rustc build failed for {s.Name}:\n{err}");

			Executables[key] = exe;
			return exe;
		}
	}

	/// <summary>Runs a prebuilt binary with optional arguments and returns its trimmed stdout.</summary>
	public static string RunExe(string baseDir, Scenario s, string exe, params string[] args)
	{
		var cwd = Path.GetDirectoryName(exe)!;
		var psi = Proc(exe, cwd);
		foreach (var arg in args)
		{
			psi.ArgumentList.Add(arg);
		}

		var (code, stdout, err) = Run(psi);
		if (code != 0)
			throw new InvalidOperationException($"'{Path.GetFileName(exe)}' exited with {code}:\n{stdout}\n{err}");
		return stdout.Trim();
	}

	/// <summary>Counts !tbaa occurrences in the -O0 emitted IR for a scenario (cache by name).</summary>
	public static int CountTbaa(string baseDir, Scenario s)
    {
        lock (Sync)
        {
            var key = $"tbaa:{s.Name}";
            if (Executables.TryGetValue(key, out var countStr)) return int.Parse(countStr);

            var cwd = ScenarioPaths.Directory(baseDir, s);
            var psi = Proc(LocateCvolo(), cwd);
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(ScenarioPaths.Cvl(baseDir, s));
            psi.ArgumentList.Add("--llvm");
            psi.ArgumentList.Add("--emit-ir");
            psi.ArgumentList.Add("-O0");
            var (code, stdout, err) = Run(psi);
            if (code != 0)
                throw new InvalidOperationException($"Cvolo IR emission failed for {s.Name}:\n{err}\n{stdout}");

            var count = Regex.Matches(stdout, "\\!tbaa").Count;
            Executables[key] = count.ToString();
            return count;
        }
    }

    private static (int Code, string Out, string Err) Run(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    private static ProcessStartInfo Proc(string exe, string workDir) => new()
    {
        FileName = exe,
        WorkingDirectory = workDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
}
