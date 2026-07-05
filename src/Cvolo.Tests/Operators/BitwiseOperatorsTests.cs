namespace Cvolo.Tests.Operators;

public sealed class BitwiseOperatorsTests : CompilerTestBase
{
	[Fact]
	public void Compiler_BitwiseAnd_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("BitwiseOperators/BitwiseAnd.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_BitwiseAnd_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("BitwiseOperators/BitwiseAnd.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("BitwiseAnd", "BitwiseOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("8", stdout);
	}

	[Fact]
	public void Compiler_BitwiseOr_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("BitwiseOperators/BitwiseOr.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_BitwiseOr_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("BitwiseOperators/BitwiseOr.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("BitwiseOr", "BitwiseOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("14", stdout);
	}

	[Fact]
	public void Compiler_BitwiseXor_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("BitwiseOperators/BitwiseXor.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_BitwiseXor_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("BitwiseOperators/BitwiseXor.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("BitwiseXor", "BitwiseOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("6", stdout);
	}

	[Fact]
	public void Compiler_BitwiseNot_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("BitwiseOperators/BitwiseNot.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_BitwiseNot_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("BitwiseOperators/BitwiseNot.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("BitwiseNot", "BitwiseOperators");
		Assert.Equal(0, runCode);
		Assert.Contains("-13", stdout);
	}
}
