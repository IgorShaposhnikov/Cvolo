using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class SandboxTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnsafeModifierBasic", "30")]
	[InlineData("UnboundModifierBasic", "30")]
	[InlineData("NoAliasOnUnbound", "30")]
	public void ModifierExecution(string caseName, string expected)
	{
		var fileName = $"Sandbox/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Sandbox");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Fact]
	public void InlineUnsafeBlock_Compile_And_Run()
	{
		var fileName = "Sandbox/InlineUnsafeBlock.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("InlineUnsafeBlock", "Sandbox");
		Assert.Equal(0, runCode);
		Assert.Contains("Result: 42", runStdout);
	}

	[Fact]
	public void UnsafePointerDeref_Compile_And_Run()
	{
		var fileName = "Sandbox/UnsafeFunctionCall.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("UnsafeFunctionCall", "Sandbox");
		Assert.Equal(0, runCode);
		Assert.Contains("Result: 42", runStdout);
	}

	[Theory]
	[InlineData("DerefOutsideUnsafe", "Cannot dereference outside unsafe context.")]
	[InlineData("AddrOfOutsideUnsafe", "Cannot take address outside unsafe context.")]
	[InlineData("RawPtrDeclOutsideUnsafe", "Raw pointer variables cannot be declared outside unsafe context.")]
	public void SandboxViolations(string caseName, string expectedError)
	{
		var fileName = $"Sandbox/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void UnboundNoRefParams_Warns_But_Still_Compile_And_Run()
	{
		var fileName = "Sandbox/UnboundNoRefParams.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.Equal(0, exitCode);
		Assert.Contains("'unbound' modifier has no effect because function has no ref/refvar parameters.", stderr);

		var (runCode, runStdout) = ExecuteBinary("UnboundNoRefParams", "Sandbox");
		Assert.Equal(0, runCode);
		Assert.Contains("30", runStdout);
	}
}
