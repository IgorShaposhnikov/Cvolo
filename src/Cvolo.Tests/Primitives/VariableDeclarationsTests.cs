namespace Cvolo.Tests.Primitives;

public sealed class VariableDeclarationsTests : CompilerTestBase
{
	[Fact]
	public void Compiler_ValInference_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("VariableDeclarations/ValInference.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors, "Expected successful compilation of ValInference.");
	}

	[Fact]
	public void E2E_ValInference_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("VariableDeclarations/ValInference.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("ValInference", "VariableDeclarations");
		Assert.Equal(0, runCode);
		Assert.Contains("100", stdout);
	}

	[Fact]
	public void Compiler_VarInference_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("VariableDeclarations/VarInference.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors, "Expected successful compilation of VarInference.");
	}

	[Fact]
	public void E2E_VarInference_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("VariableDeclarations/VarInference.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("VarInference", "VariableDeclarations");
		Assert.Equal(0, runCode);
		Assert.Contains("300", stdout);
	}

	[Fact]
	public void Compiler_ValType_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("VariableDeclarations/ValType.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors, "Expected successful compilation of ValType.");
	}

	[Fact]
	public void E2E_ValType_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("VariableDeclarations/ValType.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("ValType", "VariableDeclarations");
		Assert.Equal(0, runCode);
		Assert.Contains("400", stdout);
	}

	[Fact]
	public void Compiler_VarType_Should_Compile()
	{
		var (ast, context) = AnalyzeProject("VariableDeclarations/VarType.cvl");
		Assert.NotNull(ast);
		Assert.False(context.Diagnostics.HasErrors, "Expected successful compilation of VarType.");
	}

	[Fact]
	public void E2E_VarType_Should_Execute_With_Correct_Output()
	{
		var (compileCode, _, _) = RunCompiler("VariableDeclarations/VarType.cvl");
		Assert.Equal(0, compileCode);

		var (runCode, stdout) = ExecuteBinary("VarType", "VariableDeclarations");
		Assert.Equal(0, runCode);
		Assert.Contains("600", stdout);
	}
}
