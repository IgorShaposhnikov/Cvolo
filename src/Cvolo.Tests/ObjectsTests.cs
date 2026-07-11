using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ObjectsTests : CompilerTestBase
{
	[Theory]
	[InlineData("StructInit", "10,20")]
	[InlineData("StructNested", "15,20")]
	[InlineData("ExtensionSuccess", "Point Coords: X = 15, Y = 25")]
	public void Structs(string caseName, string expected)
	{
		var fileName = $"Objects/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Objects");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("ExtensionMutabilityFail", "No overload of function 'p.Move' matches")]
	public void Extension_Methods_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Objects/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
