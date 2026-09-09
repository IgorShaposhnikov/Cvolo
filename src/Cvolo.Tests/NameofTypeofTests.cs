using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class NameofTypeofTests : CompilerTestBase
{
	private const string Category = "CompileTimeOps";

	[Theory]
	[InlineData("NameofInstance", "X\nY\n")]
	[InlineData("NameofStatic", "X\nY\n")]
	[InlineData("NameofLocal", "localVar\nsecond\n")]
	[InlineData("TypeofFields", "3936939825\nPoint\n2515107422\nint\n")]
	public void CompileTimeOps_CompileAndRun(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("NameofMissingFail", "CVL2100")]
	[InlineData("NameofAnonymousFail", "CVL2102")]
	[InlineData("TypeofInvalidFail", "CVL2101")]
	public void CompileTimeOps_BadInputRejected(string caseName, string expectedId)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
	}
}
