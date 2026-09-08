using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class StringsTests : CompilerTestBase
{
	private const string Category = "Strings";

	[Theory]
	[InlineData("RawStrings", "C:\\deps\\win\nHe said \"hi\"\nline1\nline2\n")]
	[InlineData("RawInterpolation", "Value: 42\nHe said \"hi\"!\n")]
	[InlineData("StringConcatConst", "Hello, world\nHello, World!\n")]
	[InlineData("StringToCharPointerUnsafe", "H\n")]
	public void Strings_CompileAndRun(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("StringConcatDynamicFail", "CVL2001")]
	[InlineData("StringToCharPointerFail", "CVL2002")]
	[InlineData("RawStringUnterminatedFail", "CVL2000")]
	public void Strings_BadInputRejected(string caseName, string expectedId)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
	}
}
