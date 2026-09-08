using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class AsmTests : CompilerTestBase
{
	private const string Category = "Asm";

	[Theory]
	[InlineData("AsmNop", "ok\n")]
	public void Asm_CompileAndRun(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("AsmBswap")]
	[InlineData("AsmOutByte")]
	[InlineData("AsmVolatile")]
	public void Asm_CompilesOnly(string caseName)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
	}

	[Theory]
	[InlineData("AsmNotUnsafeFail", "CVL1600")]
	[InlineData("AsmInvalidClobberFail", "CVL1602")]
	[InlineData("AsmMultiOutputFail", "CVL1603")]
	public void Asm_BadInputRejected(string caseName, string expectedId)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
	}
}
