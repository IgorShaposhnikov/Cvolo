using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnsafeSandboxTests : CompilerTestBase
{
	[Fact]
	public void UnsafeModifierBasic_Compile_And_Run()
	{
		var fileName = "Sandbox/Unsafe/UnsafeModifierBasic.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("UnsafeModifierBasic", "Sandbox/Unsafe");
		Assert.Equal(0, runCode);
		Assert.Contains("30", runStdout);
	}

	[Fact]
	public void InlineUnsafeBlock_Compile_And_Run()
	{
		var fileName = "Sandbox/Unsafe/InlineUnsafeBlock.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("InlineUnsafeBlock", "Sandbox/Unsafe");
		Assert.Equal(0, runCode);
		Assert.Contains("Result: 42", runStdout);
	}

	[Fact]
	public void UnsafePointerDeref_Compile_And_Run()
	{
		var fileName = "Sandbox/Unsafe/UnsafeFunctionCall.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("UnsafeFunctionCall", "Sandbox/Unsafe");
		Assert.Equal(0, runCode);
		Assert.Contains("Result: 42", runStdout);
	}

	[Theory]
	[InlineData("DerefOutsideUnsafe", "Cannot dereference outside unsafe context.")]
	[InlineData("AddrOfOutsideUnsafe", "Cannot take address outside unsafe context.")]
	[InlineData("RawPtrDeclOutsideUnsafe", "Raw pointer variables cannot be declared outside unsafe context.")]
	[InlineData("UnsafeCallFromSafe", "Cannot call unsafe function 'Compute' from safe code.")]
	public void SandboxViolations(string caseName, string expectedError)
	{
		var fileName = $"Sandbox/Unsafe/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
