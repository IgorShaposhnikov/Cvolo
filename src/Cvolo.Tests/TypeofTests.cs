using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class TypeofTests : CompilerTestBase
{
	private const string Category = "Typeof";

	[Theory]
	[InlineData("FieldAccess", "3936939825\nPoint\n")]
	[InlineData("Interpolation", "type: Point with id [3936939825]\n")]
	[InlineData("AssignOnly", "")]
	[InlineData("PassByValue", "Point\n")]
	public void Typeof_CompileAndRun(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}