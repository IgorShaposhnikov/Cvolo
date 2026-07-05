namespace Cvolo.Tests.Operators;

public sealed class BitwiseCompoundTests : CompilerTestBase
{
	[Fact]
	public void Compiler_AndAssign_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("BitwiseCompound/AndAssign.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_AndAssign_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("BitwiseCompound/AndAssign.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("AndAssign", "BitwiseCompound");
		Assert.Equal(0, runCode);
		Assert.Contains("8", stdout);
	}

	[Fact]
	public void Compiler_OrAssign_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("BitwiseCompound/OrAssign.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_OrAssign_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("BitwiseCompound/OrAssign.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("OrAssign", "BitwiseCompound");
		Assert.Equal(0, runCode);
		Assert.Contains("14", stdout);
	}

	[Fact]
	public void Compiler_XorAssign_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("BitwiseCompound/XorAssign.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_XorAssign_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("BitwiseCompound/XorAssign.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("XorAssign", "BitwiseCompound");
		Assert.Equal(0, runCode);
		Assert.Contains("6", stdout);
	}

	[Fact]
	public void Compiler_LShiftAssign_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("BitwiseCompound/LShiftAssign.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors);
	}

	[Fact]
	public void E2E_LShiftAssign_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("BitwiseCompound/LShiftAssign.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("LShiftAssign", "BitwiseCompound");
		Assert.Equal(0, runCode);
		Assert.Contains("16", stdout);
	}
}
