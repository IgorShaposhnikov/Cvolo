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

	[Fact]
	public void SuppressAutoInferWarning_Hides_CVL1011_AndCompiles()
	{
		var fileName = "Lifecycle/SuppressAutoInferWarning.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("CVL1011", stderr);

		var (runCode, runStdout) = ExecuteBinary("SuppressAutoInferWarning", "Lifecycle");
		Assert.Equal(0, runCode);
		Assert.Contains("100", runStdout);
	}

	[Theory]
	[InlineData("ReceiverVarOnValFail", "No overload of function 'p.Move' matches")]
	[InlineData("ReceiverRefMutatesFail", "declares read-only 'ref this' receiver but mutates field(s)")]
	[InlineData("StrictMutabilityFail", "must declare 'ref this' or 'refvar this' receiver in [StrictMutability] struct")]
	[InlineData("FreeFnReceiverFail", "Receiver parameter ('refvar this' / 'ref this') is only allowed on extension methods")]
	public void Rejections(string caseName, string expectedError)
	{
		var fileName = $"Lifecycle/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
