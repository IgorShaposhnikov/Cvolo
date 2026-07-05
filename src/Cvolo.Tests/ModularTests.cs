using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ModularTests : CompilerTestBase
{
	[Fact]
	public void Compiler_MultiFile_Should_Compile_With_Namespaces()
	{
		// Analyze the entire 'App' directory
		var (ast, context) = AnalyzeProject("Modular/App");

		Assert.NotNull(ast);
		Assert.Equal(2, ast.Count); // Ensure both files were parsed
		Assert.False(context.Diagnostics.HasErrors, "Expected successful cross-file namespace compilation.");
	}

	[Fact]
	public void E2E_MultiFile_Should_Execute_With_Correct_Output()
	{
		// Compile the entire 'App' directory
		var (compileCode, stdout, stderr) = RunCompiler("Modular/App");
		AssertCompilationSucceeded(compileCode, stdout, stderr, "Modular/App");

		// Execute the binary (The output binary takes the name of the folder: 'App')
		var (runCode, runStdout) = ExecuteBinary("App", "Modular/App");

		Assert.Equal(0, runCode);
		Assert.Contains("Modular Vector: 10, 20", runStdout);
	}
}
