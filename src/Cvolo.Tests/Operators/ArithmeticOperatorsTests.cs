using Xunit;

namespace Cvolo.Tests.Operators;

public sealed class ArithmeticOperatorsTests : CompilerTestBase
{
	[Fact]
	public void Compiler_Add_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("ArithmeticOperators/Add.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_Add_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("ArithmeticOperators/Add.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("Add", "ArithmeticOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("30", stdout);
	}

	[Fact]
	public void Compiler_Sub_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("ArithmeticOperators/Sub.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_Sub_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("ArithmeticOperators/Sub.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("Sub", "ArithmeticOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("20", stdout);
	}

	[Fact]
	public void Compiler_Mul_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("ArithmeticOperators/Mul.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_Mul_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("ArithmeticOperators/Mul.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("Mul", "ArithmeticOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("30", stdout);
	}

	[Fact]
	public void Compiler_Div_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("ArithmeticOperators/Div.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_Div_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("ArithmeticOperators/Div.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("Div", "ArithmeticOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("5", stdout);
	}

	[Fact]
	public void Compiler_Mod_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("ArithmeticOperators/Mod.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_Mod_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("ArithmeticOperators/Mod.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("Mod", "ArithmeticOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("1", stdout);
	}
}
