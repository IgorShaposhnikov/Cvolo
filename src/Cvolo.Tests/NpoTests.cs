using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class NpoTests : CompilerTestBase
{
	[Theory]
	[InlineData("NpoFlatPtr", "Flat: 42\nNone created.")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"NPO/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "NPO");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}
