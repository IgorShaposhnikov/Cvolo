using System.Diagnostics;
using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Tests.Core;

public abstract class CompilerTestBase
{
	protected const string TestCasesDirectory = Constants.TestCasesDirectory;

	// Every xUnit test instance gets its own physical isolation root. Multiple test classes may
	// compile the same .cvl fixture in parallel; sharing _isolated/<category>/<case> lets one
	// invocation delete obj/bin while another clang/lld process is still writing into it.
	private readonly string _isolationId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
	private readonly Dictionary<string, string> _isolatedCaseDirectories = new(StringComparer.OrdinalIgnoreCase);

	protected (IReadOnlyList<CompilationUnitSyntax>? AST, BindingContext Context) AnalyzeProject(string path)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(CompilerTestBase).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, TestCasesDirectory, path);

		var project = CompilationProject.Load(fullPath);
		var asts = new List<CompilationUnitSyntax>();
		var parser = new AntlrSyntaxParser();
		var binder = new Binder();

		foreach (var file in project.SourceFiles)
		{
			var sourceCode = File.ReadAllText(file);
			var context = new CompilationContext(sourceCode, file);
			var ast = parser.Parse(context);

			if (ast is not null)
			{
				asts.Add(ast);
				binder.Context.FileContexts[ast] = context;
			}
		}

		binder.Context.BaseSourcePaths.UnionWith(project.BaseSourceFiles.Select(Path.GetFullPath));

		binder.Bind(asts);
		return (asts, binder.Context);
	}

	protected (int ExitCode, string StdOut, string StdErr) RunCompiler(string sourcePath, params string[] extraArgs)
	{
		return RunCompilerCore(sourcePath, useCheck: false, extraArgs);
	}

	protected (int ExitCode, string StdOut, string StdErr) RunCompilerCheck(string sourcePath, params string[] extraArgs)
	{
		return RunCompilerCore(sourcePath, useCheck: true, extraArgs);
	}

	private (int ExitCode, string StdOut, string StdErr) RunCompilerCore(string sourcePath, bool useCheck, string[] extraArgs)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(CompilerTestBase).Assembly.Location)!;
		var compilerExe = Path.Combine(assemblyDir, OperatingSystem.IsWindows() ? "Cvolo.exe" : "Cvolo");
		var fullSourcePath = Path.Combine(assemblyDir, TestCasesDirectory, sourcePath);

		// Single-file cases (.cvl extension) are staged into an isolated folder
		// before invoking the CLI: the compiler compiles whole directories, so
		// sibling test cases redeclaring Main/types would poison the build.
		// Extension-less paths are directory projects and keep those semantics.
		if (Path.GetExtension(sourcePath) == ".cvl")
		{
			fullSourcePath = IsolateSingleFileCase(assemblyDir, sourcePath);
		}

		var command = useCheck ? "check" : "build";
		var psi = new ProcessStartInfo
		{
			FileName = compilerExe,
			Arguments = $"{command} \"{fullSourcePath}\" {string.Join(" ", extraArgs)}",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var proc = Process.Start(psi)!;
		var stdoutTask = proc.StandardOutput.ReadToEndAsync();
		var stderr = proc.StandardError.ReadToEnd();
		proc.WaitForExit();

		var stdout = stdoutTask.GetAwaiter().GetResult();

		return (proc.ExitCode, stdout, stderr);
	}

	private string IsolateSingleFileCase(string assemblyDir, string sourcePath)
	{
		var sourceRoot = Path.Combine(assemblyDir, TestCasesDirectory);
		var fullSourcePath = Path.Combine(sourceRoot, sourcePath);
		var caseName = Path.GetFileNameWithoutExtension(sourcePath);
		var isolatedDir = GetOrCreateIsolatedCaseDirectory(sourcePath);

		// Repeated compiles inside the same test instance intentionally reuse the same root so
		// companion .obj/.o fixtures placed beside the staged source survive. Only compiler outputs
		// are reset between invocations; other test instances have different roots and cannot race.
		var isolatedObj = Path.Combine(isolatedDir, "obj");
		var isolatedBin = Path.Combine(isolatedDir, "bin");
		if (Directory.Exists(isolatedObj))
			Directory.Delete(isolatedObj, recursive: true);
		if (Directory.Exists(isolatedBin))
			Directory.Delete(isolatedBin, recursive: true);

		var stagedSource = Path.Combine(isolatedDir, caseName + ".cvl");
		File.Copy(fullSourcePath, stagedSource, overwrite: true);
		File.SetLastWriteTimeUtc(stagedSource, DateTime.UtcNow);
		return stagedSource;
	}

	/// <summary>
	/// Returns the per-test isolation directory for one single-file fixture, creating it if needed.
	/// Tests that need to place native companion objects beside the staged .cvl use this helper so
	/// their inputs and the compiler invocation share the same unique root.
	/// </summary>
	protected string GetOrCreateIsolatedCaseDirectory(string sourcePath)
	{
		var key = NormalizeSourcePathKey(sourcePath);
		if (_isolatedCaseDirectories.TryGetValue(key, out var existing))
			return existing;

		var assemblyDir = Path.GetDirectoryName(typeof(CompilerTestBase).Assembly.Location)!;
		var sourceRoot = Path.Combine(assemblyDir, TestCasesDirectory);
		var categoryDir = Path.GetDirectoryName(key) ?? string.Empty;
		var caseName = Path.GetFileNameWithoutExtension(key);
		var isolatedDir = Path.GetFullPath(Path.Combine(
			sourceRoot, "_isolated", categoryDir, caseName, _isolationId));

		Directory.CreateDirectory(isolatedDir);
		_isolatedCaseDirectories[key] = isolatedDir;
		return isolatedDir;
	}

	/// <summary>Returns an artifact path produced by the most recent compile of a fixture in this test instance.</summary>
	protected string GetCompilationArtifactPath(string sourcePath, params string[] relativeSegments)
	{
		var root = GetOrCreateIsolatedCaseDirectory(sourcePath);
		var path = root;
		foreach (var segment in relativeSegments)
			path = Path.Combine(path, segment);
		return path;
	}

	private static string NormalizeSourcePathKey(string sourcePath)
	{
		var normalized = sourcePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
		return normalized.TrimStart(Path.DirectorySeparatorChar);
	}

	private string ResolveBinaryPath(string assemblyDir, string binaryName, string folderPath)
	{
		var binaryExt = OperatingSystem.IsWindows() ? ".exe" : "";
		var binaryFileName = binaryName + binaryExt;

		var logicalSource = NormalizeSourcePathKey(Path.Combine(folderPath, binaryName + ".cvl"));
		if (_isolatedCaseDirectories.TryGetValue(logicalSource, out var isolatedDir))
		{
			var isolatedPath = Path.Combine(isolatedDir, "bin", "Debug", binaryFileName);
			if (File.Exists(isolatedPath))
				return isolatedPath;
		}

		// Directory-project tests are not staged and keep their historical output layout.
		return Path.GetFullPath(Path.Combine(
			assemblyDir, TestCasesDirectory, folderPath, "bin", "Debug", binaryFileName));
	}

	protected (int ExitCode, string StdOut) ExecuteBinary(string binaryName, string folderPath)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(CompilerTestBase).Assembly.Location)!;
		var binaryPath = ResolveBinaryPath(assemblyDir, binaryName, folderPath);

		var psi = new ProcessStartInfo
		{
			FileName = binaryPath,
			RedirectStandardOutput = true,
			StandardOutputEncoding = System.Text.Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var proc = Process.Start(psi)!;
		var stdout = proc.StandardOutput.ReadToEnd();
		proc.WaitForExit();

		return (proc.ExitCode, stdout);
	}

	protected void AssertCompilationSucceeded(int exitCode, string stdout, string stderr, string fileName)
	{
		if (exitCode != 0)
		{
			Assert.Fail($"Compilation of '{fileName}' failed with code {exitCode}.\n\n--- STDERR ---\n{stderr}\n\n--- STDOUT ---\n{stdout}");
		}
	}

	protected (int ExitCode, string StdOut) ExecuteBinaryWithInput(string binaryName, string folderPath, string input)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(CompilerTestBase).Assembly.Location)!;
		var binaryPath = ResolveBinaryPath(assemblyDir, binaryName, folderPath);

		var psi = new ProcessStartInfo
		{
			FileName = binaryPath,
			RedirectStandardOutput = true,
			RedirectStandardInput = true,
			StandardOutputEncoding = System.Text.Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var proc = Process.Start(psi)!;

		proc.StandardInput.WriteLine(input);
		proc.StandardInput.Flush();
		proc.StandardInput.Close();

		// Use async reading if your compiled binaries print large text blobs
		var stdoutTask = proc.StandardOutput.ReadToEndAsync();
		proc.WaitForExit();

		var stdout = stdoutTask.GetAwaiter().GetResult();

		return (proc.ExitCode, stdout);
	}
}
