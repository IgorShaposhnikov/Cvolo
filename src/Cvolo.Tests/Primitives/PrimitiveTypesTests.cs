namespace Cvolo.Tests.Primitives;

public sealed class PrimitiveTypesTests : CompilerTestBase
{
	[Fact]
	public void Compiler_Int_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("Primitives/Int.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors, "Expected successful compilation of Int primitive.");
	}

	[Fact]
	public void E2E_Int_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("Primitives/Int.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("Int", "Primitives");
		Assert.Equal(0, runCode);
		Assert.Contains("10", stdout);
	}

	[Fact]
	public void Compiler_Double_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("Primitives/Double.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors, "Expected successful compilation of Double primitive.");
	}

	[Fact]
	public void E2E_Double_Should_Execute_With_Correct_Output()
	{
		var (compileCode, stdout, stderr) = RunCompiler("Primitives/Double.cvl");

		if (compileCode != 0)
		{
			Assert.Fail($"Double.cvl compilation failed.\n--- STDERR ---\n{stderr}\n--- STDOUT ---\n{stdout}");
		}

		var (runCode, runStdout) = ExecuteBinary("Double", "Primitives");
		Assert.Equal(0, runCode);
		Assert.Contains("20.5", runStdout);
	}

	[Fact]
	public void Compiler_Bool_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("Primitives/Bool.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors, "Expected successful compilation of Bool primitive.");
	}

	[Fact]
	public void E2E_Bool_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("Primitives/Bool.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("Bool", "Primitives");
		Assert.Equal(0, runCode);
		Assert.Contains("1", stdout);
	}

	[Fact]
	public void Compiler_Char_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("Primitives/Char.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors, "Expected successful compilation of Char primitive.");
	}

	[Fact]
	public void E2E_Char_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("Primitives/Char.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("Char", "Primitives");
		Assert.Equal(0, runCode);
		Assert.Contains("A", stdout);
	}
}
