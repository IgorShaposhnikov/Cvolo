using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class GlobalsTests : CompilerTestBase
{
	[Theory]
	[InlineData("GlobalCounter", "Count: 3")]
	[InlineData("GlobalStruct", "X=100, Y=200")]
	[InlineData("GlobalZeroInit", "Unset: 0")]
	public void Execution(string caseName, string expected)
	{
		var fileName = $"Globals/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Globals");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("GlobalNonConstFail", "must be initialized with a compile-time constant")]
	[InlineData("GlobalConstWriteFail", "Cannot assign to immutable variable 'ReadOnly'")]
	public void Safety_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Globals/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
