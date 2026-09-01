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

	[Fact]
	public void Increment8_DestructiveCastOnHeapHandle_Compile_And_Run()
	{
		var fileName = "Sandbox/Increment8/DestructiveCast.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("DestructiveCast", "Sandbox/Increment8");
		Assert.Equal(0, runCode);
		Assert.Contains("Point(10, 20)", runStdout);
		Assert.Contains("After move: Point(15, 15)", runStdout);
		Assert.Contains("Color is Green", runStdout);
		Assert.Contains("Node value: 42", runStdout);
	}

	[Fact]
	public void Increment8_DestructiveCastOnStackValue_Rejected()
	{
		var fileName = "Sandbox/Increment8/DestructiveCastStackVarFail.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.Equal(1, exitCode);
		Assert.Contains("requires an owning heap handle", stderr);
		Assert.Contains("'node' is a stack value", stderr);
	}
}
