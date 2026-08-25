using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class CopyMoveTests : CompilerTestBase
{
	[Theory]
	[InlineData("LargeCopyWarn")]
	[InlineData("LargeCopyInitWarn")]
	[InlineData("LargeCopyAssignWarn")]
	[InlineData("NestedLargeCopy")]
	public void EmitsCVL1003(string caseName)
	{
		var fileName = $"CopyMove/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.Contains("CVL1003", stderr);
	}

	[Theory]
	[InlineData("LargeCopyRefSilent")]
	[InlineData("LargeCopyInitNowarn", "--nowarn", "CVL1003")]
	public void NoCVL1003(string caseName, params string[] extraArgs)
	{
		var fileName = $"CopyMove/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, extraArgs);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("CVL1003", stderr);
	}

	[Theory]
	[InlineData("ResourceMoveStillMoves", "Use of moved variable 'r1'")]
	[InlineData("TransitiveResource", "Use of moved variable 'o1'")]
	public void RejectedAfterMove(string caseName, string expectedError)
	{
		var fileName = $"CopyMove/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void TrivialCopyBothActive()
	{
		var fileName = "CopyMove/TrivialCopyBothActive.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("TrivialCopyBothActive", "CopyMove");
		Assert.Equal(0, runCode);
		Assert.Contains("10 20", runStdout);
	}
}
