using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class LifecycleTests : CompilerTestBase
{
	[Theory]
	[InlineData("ReceiverVar", "40")]
	[InlineData("ReceiverRef", "30, 10")]
	[InlineData("StrictMutability", "100")]
	public void Execution(string caseName, string expected)
	{
		var fileName = $"Lifecycle/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Lifecycle");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Fact]
	public void AutoInferWarning_Emits_CVL1011()
	{
		var fileName = "Lifecycle/AutoInferWarning.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.Contains("CVL1011", stderr);
		Assert.Contains("Auto-inference chose mutability", stderr);
	}

	[Theory]
	[InlineData("ReceiverVarOnValFail", "No overload of function 'p.Move' matches")]
	[InlineData("ReceiverRefMutatesFail", "declares read-only 'ref this' receiver but mutates field(s)")]
	[InlineData("StrictMutabilityFail", "must declare 'ref this' or 'refvar this' receiver in [StrictMutability] struct")]
	public void Rejections(string caseName, string expectedError)
	{
		var fileName = $"Lifecycle/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
