using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class NpoTests : CompilerTestBase
{
	[Theory]
	[InlineData("NpoFlatPtr", "Flat: 42\nNone created.")]
	[InlineData("NpoSwitchRefPromotion", "Some: 42\nNone path")]
	[InlineData("OptionMoveDtor", "Dropping: 1\nDone")]
	[InlineData("OptionDropOnReassign", "start\nDropping: 10\nmiddle\nnow: 20\nDropping: 20")]
	[InlineData("OptionMoveToParam", "in consume, some=7\nDropping: 7\nDone")]
	[InlineData("ArrayOptionZeroInit", "zero: 0")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"NPO/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "NPO");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("NpoSwitchByValueFail", "Cannot pattern-match 'Some x' by value on a nullable reference option")]
	public void Semantic_Rejections(string caseName, string expectedError)
	{
		var fileName = $"NPO/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
