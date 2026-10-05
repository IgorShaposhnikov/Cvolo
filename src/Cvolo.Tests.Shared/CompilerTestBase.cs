using Cvolo.Analysis;
using Cvolo.Core.AST.Base;

namespace Cvolo.Tests.Shared;

/// <summary>
/// Compatibility-friendly xUnit base over <see cref="CompilerHarness"/>. New helpers belong in the
/// harness/shared test infrastructure rather than in individual test projects.
/// </summary>
public abstract class CompilerTestBase
{
	protected const string TestCasesDirectory = TestCases.DirectoryName;

	private readonly CompilerHarness _compiler = new();

	protected (IReadOnlyList<CompilationUnitSyntax>? AST, BindingContext Context) AnalyzeProject(string path) =>
		_compiler.AnalyzeProject(path);

	protected (int ExitCode, string StdOut, string StdErr) RunCompiler(string sourcePath, params string[] extraArgs)
	{
		var result = _compiler.Build(sourcePath, extraArgs);
		return (result.ExitCode, result.StdOut, result.StdErr);
	}

	protected (int ExitCode, string StdOut, string StdErr) RunCompilerCheck(string sourcePath, params string[] extraArgs)
	{
		var result = _compiler.Check(sourcePath, extraArgs);
		return (result.ExitCode, result.StdOut, result.StdErr);
	}

	protected (int ExitCode, string StdOut) ExecuteBinary(string binaryName, string folderPath)
	{
		var result = _compiler.ExecuteBinary(binaryName, folderPath);
		return (result.ExitCode, result.StdOut);
	}

	protected (int ExitCode, string StdOut) ExecuteBinaryWithInput(string binaryName, string folderPath, string input)
	{
		var result = _compiler.ExecuteBinary(binaryName, folderPath, input);
		return (result.ExitCode, result.StdOut);
	}

	protected string GetOrCreateIsolatedCaseDirectory(string sourcePath) =>
		_compiler.GetOrCreateIsolatedCaseDirectory(sourcePath);

	protected string GetCompilationArtifactPath(string sourcePath, params string[] relativeSegments) =>
		_compiler.GetCompilationArtifactPath(sourcePath, relativeSegments);

	protected static string ReadTestCaseSource(string relativePath) => TestCases.ReadText(relativePath);

	protected static void AssertCompilationSucceeded(int exitCode, string stdout, string stderr, string fileName)
	{
		if (exitCode != 0)
		{
			Xunit.Assert.Fail(
				$"Compilation of '{fileName}' failed with code {exitCode}.\n\n--- STDERR ---\n{stderr}\n\n--- STDOUT ---\n{stdout}");
		}
	}
}
