using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class PrimitiveTypesTests : CompilerTestBase
{
	[Theory]
	[InlineData("Int", "10")]
	[InlineData("Double", "20.5")]
	[InlineData("Bool", "1")]
	[InlineData("Char", "A")]
	public void Primitive(string caseName, string expectedOutput)
	{
		var fileName = $"Primitives/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Primitives");
		Assert.Equal(0, runCode);
		Assert.Contains(expectedOutput, runStdout);
	}

	[Theory]
	[InlineData("ValInference", "100")]
	[InlineData("VarInference", "300")]
	[InlineData("ValType", "400")]
	[InlineData("VarType", "600")]
	public void Variable(string caseName, string expected)
	{
		var fileName = $"VariableDeclarations/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "VariableDeclarations");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}
}
