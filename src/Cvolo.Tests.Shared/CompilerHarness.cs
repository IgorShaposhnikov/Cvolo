using System.Diagnostics;
using System.Text;
using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Tests.Shared;

/// <summary>
/// Shared compiler integration harness. It owns single-file isolation, compiler invocation,
/// generated-artifact lookup and process execution for every test project that compiles Cvolo code.
/// </summary>
public sealed class CompilerHarness
{
	private readonly string _isolationId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
	private readonly Dictionary<string, string> _isolatedCaseDirectories = new(StringComparer.OrdinalIgnoreCase);

	public (IReadOnlyList<CompilationUnitSyntax>? AST, BindingContext Context) AnalyzeProject(string relativePath)
	{
		var fullPath = TestCases.PathOf(relativePath);
		var project = CompilationProject.Load(fullPath);
		var asts = new List<CompilationUnitSyntax>();
		var parser = new AntlrSyntaxParser();
		var binder = new Binder();

		foreach (var file in project.SourceFiles)
		{
			var sourceCode = File.ReadAllText(file);
			var context = new CompilationContext(sourceCode, file);
			var ast = parser.Parse(context);
			if (ast is null)
				continue;

			asts.Add(ast);
			binder.Context.FileContexts[ast] = context;
		}

		binder.Context.BaseSourcePaths.UnionWith(project.BaseSourceFiles.Select(Path.GetFullPath));
		binder.Bind(asts);
		return (asts, binder.Context);
	}

	public CompilerInvocationResult Build(string relativePath, params string[] extraArgs) =>
		RunCompiler(relativePath, useCheck: false, extraArgs);

	public CompilerInvocationResult Check(string relativePath, params string[] extraArgs) =>
		RunCompiler(relativePath, useCheck: true, extraArgs);

	public string GetOrCreateIsolatedCaseDirectory(string relativeSourcePath)
	{
		var key = NormalizeSourcePathKey(relativeSourcePath);
		if (_isolatedCaseDirectories.TryGetValue(key, out var existing))
			return existing;

		var sourceRoot = TestCases.Root;
		var isolatedDirectory = Path.GetFullPath(Path.Combine(
			sourceRoot,
			"_isolated",
			_isolationId,
			$"case{_isolatedCaseDirectories.Count + 1:000}"));

		Directory.CreateDirectory(isolatedDirectory);
		_isolatedCaseDirectories[key] = isolatedDirectory;
		return isolatedDirectory;
	}

	public string GetCompilationArtifactPath(string relativeSourcePath, params string[] relativeSegments)
	{
		var path = GetOrCreateIsolatedCaseDirectory(relativeSourcePath);
		foreach (var segment in relativeSegments)
			path = Path.Combine(path, segment);
		return path;
	}

	public ProcessResult ExecuteBinary(string binaryName, string folderPath, string? input = null)
	{
		var binaryPath = ResolveBinaryPath(binaryName, folderPath);
		// The isolated case directory nests the case name twice (folder + exe), which can push the
		// full path past the legacy 260-character MAX_PATH limit for long case names. CreateProcess
		// cannot reliably launch those (the extended-length "\\?\" prefix does not help here, either),
		// so run the binary from a short temporary copy and remove it afterwards.
		string? temporaryCopy = null;
		var launchPath = binaryPath;
		if (binaryPath.Length >= 248)
		{
			temporaryCopy = Path.Combine(
				Path.GetTempPath(),
				$"cvolotest_{Guid.NewGuid():N}{Path.GetExtension(binaryPath)}");
			File.Copy(binaryPath, temporaryCopy, overwrite: true);
			launchPath = temporaryCopy;
		}

		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = launchPath,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = input is not null,
				StandardOutputEncoding = Encoding.UTF8,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			using var process = Process.Start(psi)
				?? throw new InvalidOperationException($"Failed to start compiled test binary '{binaryPath}'.");

			if (input is not null)
			{
				process.StandardInput.WriteLine(input);
				process.StandardInput.Flush();
				process.StandardInput.Close();
			}

			var stdoutTask = process.StandardOutput.ReadToEndAsync();
			var stderrTask = process.StandardError.ReadToEndAsync();
			process.WaitForExit();

			return new ProcessResult(
				process.ExitCode,
				stdoutTask.GetAwaiter().GetResult(),
				stderrTask.GetAwaiter().GetResult());
		}
		finally
		{
			if (temporaryCopy is not null)
			{
				try { File.Delete(temporaryCopy); }
				catch { /* best-effort cleanup */ }
			}
		}
	}

	private CompilerInvocationResult RunCompiler(string relativePath, bool useCheck, string[] extraArgs)
	{
		var compilerExecutable = Path.Combine(
			AppContext.BaseDirectory,
			OperatingSystem.IsWindows() ? "Cvolo.exe" : "Cvolo");
		var fullSourcePath = TestCases.PathOf(relativePath);

		if (Path.GetExtension(relativePath).Equals(".cvl", StringComparison.OrdinalIgnoreCase))
			fullSourcePath = IsolateSingleFileCase(relativePath);

		var psi = new ProcessStartInfo
		{
			FileName = compilerExecutable,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add(useCheck ? "check" : "build");
		psi.ArgumentList.Add(fullSourcePath);
		foreach (var argument in extraArgs)
			psi.ArgumentList.Add(argument);

		using var process = Process.Start(psi)
			?? throw new InvalidOperationException($"Failed to start compiler '{compilerExecutable}'.");
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		process.WaitForExit();

		return new CompilerInvocationResult(
			process.ExitCode,
			stdoutTask.GetAwaiter().GetResult(),
			stderrTask.GetAwaiter().GetResult(),
			fullSourcePath);
	}

	private string IsolateSingleFileCase(string relativeSourcePath)
	{
		var fullSourcePath = TestCases.PathOf(relativeSourcePath);
		var caseName = Path.GetFileNameWithoutExtension(relativeSourcePath);
		var isolatedDirectory = GetOrCreateIsolatedCaseDirectory(relativeSourcePath);

		// Repeated compiles inside one test instance reuse the same root so companion .obj/.o files
		// survive. Compiler output alone is reset between invocations.
		foreach (var outputDirectoryName in new[] { "obj", "bin" })
		{
			var outputDirectory = Path.Combine(isolatedDirectory, outputDirectoryName);
			if (Directory.Exists(outputDirectory))
				Directory.Delete(outputDirectory, recursive: true);
		}

		var stagedSource = Path.Combine(isolatedDirectory, caseName + ".cvl");
		File.Copy(fullSourcePath, stagedSource, overwrite: true);
		File.SetLastWriteTimeUtc(stagedSource, DateTime.UtcNow);
		return stagedSource;
	}

	private string ResolveBinaryPath(string binaryName, string folderPath)
	{
		var binaryFileName = binaryName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
		var logicalSource = NormalizeSourcePathKey(Path.Combine(folderPath, binaryName + ".cvl"));
		if (_isolatedCaseDirectories.TryGetValue(logicalSource, out var isolatedDirectory))
		{
			var isolatedPath = Path.Combine(isolatedDirectory, "bin", "Debug", binaryFileName);
			if (File.Exists(isolatedPath))
				return isolatedPath;
		}

		return Path.GetFullPath(Path.Combine(TestCases.Root, folderPath, "bin", "Debug", binaryFileName));
	}

	private static string NormalizeSourcePathKey(string sourcePath) =>
		sourcePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
			.TrimStart(Path.DirectorySeparatorChar);
}

public sealed record CompilerInvocationResult(
	int ExitCode,
	string StdOut,
	string StdErr,
	string CompiledPath);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
