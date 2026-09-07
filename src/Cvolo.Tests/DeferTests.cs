using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class DeferTests : CompilerTestBase
{
	[Theory]
	[InlineData("Lifo", "321")]
	[InlineData("EarlyReturn", "TDFD")]
	[InlineData("Nested", "XBA")]
	public void DeferBehavior(string caseName, string expected)
	{
		var fileName = $"Defer/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Defer");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}
}
