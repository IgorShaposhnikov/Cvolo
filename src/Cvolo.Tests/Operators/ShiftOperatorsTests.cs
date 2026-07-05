namespace Cvolo.Tests.Operators;

public sealed class ShiftOperatorsTests : CompilerTestBase
{
	[Fact]
	public void Compiler_LShift_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("ShiftOperators/LShift.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_LShift_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("ShiftOperators/LShift.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("LShift", "ShiftOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("8", stdout);
	}

	[Fact]
	public void Compiler_RShift_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("ShiftOperators/RShift.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_RShift_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("ShiftOperators/RShift.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("RShift", "ShiftOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("4", stdout);
	}
}
