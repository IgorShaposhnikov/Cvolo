using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class FinallyTests : CompilerTestBase
{
	[Theory]
	[InlineData("FinallyOnSuccess", "BFA2")]
	[InlineData("FinallyOnError", "CFA333")]
	[InlineData("FinallyWithReturn", "BFA7")]
	[InlineData("FinallyObservesLiveState", "22")]
	[InlineData("FinallyWithDefer", "TDFA")]
	[InlineData("FinallyNested", "GH222")]
	public void FinallyBehavior(string caseName, string expected)
	{
		var fileName = $"Finally/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Finally");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("FinallyReturnLeak", "CVL1063", "Control flow cannot leave a `finally` block")]
	[InlineData("FinallyDeferNested", "CVL1064", "`defer` cannot be nested inside a `finally` block")]
	public void FinallyRejections(string caseName, string expectedId, string expectedMessage)
	{
		var (exitCode, _, stderr) = RunCompiler($"Finally/{caseName}.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
		Assert.Contains(expectedMessage, stderr);
	}
}