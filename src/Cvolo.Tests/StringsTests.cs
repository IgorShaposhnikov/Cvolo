using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class StringsTests : CompilerTestBase
{
	[Theory]
	[InlineData("StringInterpolation", "10,20,30")]
	[InlineData("StringInterpolationMultiTypes", "Name: Cvolo, Int: 42, Float: 3.140000, Bool: 1, Char: A")]
	public void String_Features(string caseName, string expected)
	{
		var fileName = $"Strings/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Strings");
		Assert.Equal(0, runCode);

		// Normalize line endings to avoid cross-platform CRLF (\r\n) vs LF (\n) match failures
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}
