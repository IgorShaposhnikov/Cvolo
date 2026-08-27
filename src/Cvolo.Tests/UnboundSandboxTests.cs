using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnboundSandboxTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnboundModifierBasic", "30")]
	[InlineData("NoAliasOnUnbound", "30")]
	public void ModifierExecution(string caseName, string expected)
	{
		var fileName = $"Sandbox/Unbound/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Sandbox/Unbound");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Fact]
	public void UnboundNoRefParams_Warns_But_Still_Compile_And_Run()
	{
		var fileName = "Sandbox/Unbound/UnboundNoRefParams.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.Equal(0, exitCode);
		Assert.Contains("'unbound' modifier has no effect because function has no ref/refvar parameters.", stderr);

		var (runCode, runStdout) = ExecuteBinary("UnboundNoRefParams", "Sandbox/Unbound");
		Assert.Equal(0, runCode);
		Assert.Contains("30", runStdout);
	}
}
