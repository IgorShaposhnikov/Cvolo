using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class MemoryTests : CompilerTestBase
{
	[Theory]
	[InlineData("HeapRaii", "100")]
	[InlineData("BorrowMutable", "100")]
	[InlineData("MoveReassign", "20")]
	[InlineData("DynamicHeapArray", "Buffer 0: 10, Buffer 4: 50, Length: 5")]
	[InlineData("SizeOf", "Int: 4\nDouble: 8\nPoint: 8\nSlice: 16")]
	[InlineData("AutoDestructor", "Disposing resource handle: 100\nDisposing resource handle: 200\nDone!")]
	public void Execution(string caseName, string expected)
	{
		var fileName = $"Memory/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Memory");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("MoveFail", "Use of moved variable 'r1'")]
	[InlineData("BorrowFail", "Cannot borrow 'p' because an incompatible borrow is already active")]
	[InlineData("DanglingFail", "Cannot return reference to local variable 'p'")]
	[InlineData("MoveHeapArrayFail", "Use of moved variable 'buffer'")]
	public void SafetyRejections(string caseName, string expectedError)
	{
		var fileName = $"Memory/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		// Assert that compilation correctly fails
		Assert.Equal(1, exitCode);

		// Assert that the exact security error is printed
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void E2E_Qualified_Namespace_Call_Should_Resolve_And_Execute()
	{
		// 1. Compile the entire namespace directory
		var fileName = "Modular/ConsoleNamespace";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		// 2. Execute the output binary (takes the folder name 'ConsoleNamespace')
		var (runCode, runStdout) = ExecuteBinary("ConsoleNamespace", "Modular/ConsoleNamespace");
		Assert.Equal(0, runCode);
		Assert.Contains("Value: 123", runStdout);
	}
}
