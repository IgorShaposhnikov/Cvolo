using System.Diagnostics;
using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax;

namespace Cvolo.Tests;

public abstract class CompilerTestBase
{
	protected const string TestCasesDirectory = "TestCases";

	protected (IReadOnlyList<CompilationUnitSyntax>? AST, BindingContext Context) AnalyzeProject(string path)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(CompilerTestBase).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, TestCasesDirectory, path);

		var project = CompilationProject.Load(fullPath);
		var asts = new List<CompilationUnitSyntax>();
		var parser = new SyntaxParser();
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

		var psi = new ProcessStartInfo
		{
			FileName = compilerExe,
			Arguments = $"\"{fullSourcePath}\"",
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

	protected (int ExitCode, string StdOut) ExecuteBinary(string binaryName, string folderPath)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(CompilerTestBase).Assembly.Location)!;
		var binaryDir = Path.GetFullPath(Path.Combine(assemblyDir, TestCasesDirectory, folderPath, "bin", "Debug"));

		var binaryExt = OperatingSystem.IsWindows() ? ".exe" : "";
		var binaryPath = Path.Combine(binaryDir, binaryName + binaryExt);

		var psi = new ProcessStartInfo
		{
			FileName = binaryPath,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var proc = Process.Start(psi)!;
		var stdout = proc.StandardOutput.ReadToEnd();
		proc.WaitForExit();

		return (proc.ExitCode, stdout);
	}
}
