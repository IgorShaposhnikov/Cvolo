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

		binder.Bind(asts);
		return (asts, binder.Context);
	}

	protected (int ExitCode, string StdOut, string StdErr) RunCompiler(string sourcePath)
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

		var psi = new ProcessStartInfo
		{
			FileName = compilerExe,
			Arguments = $"build \"{fullSourcePath}\"",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var proc = Process.Start(psi)!;
		var stdout = proc.StandardOutput.ReadToEnd();
		var stderr = proc.StandardError.ReadToEnd();
		proc.WaitForExit();

		return (proc.ExitCode, stdout, stderr);
	}

	private string IsolateSingleFileCase(string assemblyDir, string sourcePath)
	{
		var sourceRoot = Path.Combine(assemblyDir, TestCasesDirectory);
		var fullSourcePath = Path.Combine(sourceRoot, sourcePath);
		var categoryDir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
		var caseName = Path.GetFileNameWithoutExtension(sourcePath);
		var isolatedDir = Path.GetFullPath(Path.Combine(sourceRoot, "_isolated", categoryDir, caseName));

		Directory.CreateDirectory(isolatedDir);
		File.Copy(fullSourcePath, Path.Combine(isolatedDir, caseName + ".cvl"), overwrite: true);
		return Path.Combine(isolatedDir, caseName + ".cvl");
	}

	private string ResolveBinaryPath(string assemblyDir, string binaryName, string folderPath)
	{
		var binaryExt = OperatingSystem.IsWindows() ? ".exe" : "";
		var binaryFileName = binaryName + binaryExt;

		var isolatedPath = Path.GetFullPath(Path.Combine(
			assemblyDir, TestCasesDirectory, "_isolated", folderPath, binaryName, "bin", "Debug", binaryFileName));
		if (File.Exists(isolatedPath))
		{
			return isolatedPath;
		}

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

		var stdout = proc.StandardOutput.ReadToEnd();
		proc.WaitForExit();

		return (proc.ExitCode, stdout);
	}
}
