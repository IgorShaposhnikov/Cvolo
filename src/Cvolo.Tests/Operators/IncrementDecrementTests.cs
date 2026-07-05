using Xunit;

namespace Cvolo.Tests.Operators;

public sealed class IncrementDecrementTests : CompilerTestBase
{
	[Fact]
	public void Compiler_PrefixInc_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("IncrementDecrement/PrefixInc.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_PrefixInc_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("IncrementDecrement/PrefixInc.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("PrefixInc", "IncrementDecrement");
		Assert.Equal(0, runCode);
		Assert.Contains("11,11", stdout);
	}

	[Fact]
	public void Compiler_PostfixInc_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("IncrementDecrement/PostfixInc.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_PostfixInc_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("IncrementDecrement/PostfixInc.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("PostfixInc", "IncrementDecrement");
		Assert.Equal(0, runCode);
		Assert.Contains("11,10", stdout);
	}

	[Fact]
	public void Compiler_PrefixDec_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("IncrementDecrement/PrefixDec.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_PrefixDec_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("IncrementDecrement/PrefixDec.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("PrefixDec", "IncrementDecrement");
		Assert.Equal(0, runCode);
		Assert.Contains("9,9", stdout);
	}

	[Fact]
	public void Compiler_PostfixDec_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("IncrementDecrement/PostfixDec.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_PostfixDec_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("IncrementDecrement/PostfixDec.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("PostfixDec", "IncrementDecrement");
		Assert.Equal(0, runCode);
		Assert.Contains("9,10", stdout);
	}
}
