using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class NamespacesTests : CompilerTestBase
{
	private const string Category = "Namespaces";

	[Theory]
	[InlineData("ExposeUsingBasic", "PI: 3.141590\nSqrt: 1.732051\nVec: 1.000000, 2.000000")]
	[InlineData("ExposeUsingTransitive", "Answer: 42")]
	[InlineData("ExposeUsingCycle", "Sum: 30")]
	public void ExposeUsing_Execution_Success(string caseName, string expected)
	{
		var (compileCode, stdout, stderr) = RunCompiler($"{Category}/{caseName}");
		AssertCompilationSucceeded(compileCode, stdout, stderr, $"{Category}/{caseName}");

		var (runCode, runStdout) = ExecuteBinary(caseName, $"{Category}/{caseName}");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("ExposeUsingOutsideNamespaceFail.cvl", "CVL1060", "'expose using' can only be used inside a namespace")]
	[InlineData("ExposeUsingNotFoundFail.cvl", "CVL1061", "Target namespace 'NonExistent.SubModule' of 'expose using' does not exist")]
	public void ExposeUsing_Rejections(string caseName, string expectedCode, string expectedError)
	{
		var (exitCode, stdout, stderr) = RunCompiler($"{Category}/{caseName}");

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedCode, stderr);
		Assert.Contains(expectedError, stderr);
	}
}
