using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class FlowTests : CompilerTestBase
{
	[Theory]
	[InlineData("IfElse", "Greater")]
	[InlineData("WhileLoop", "012")]
	[InlineData("ForLoop", "012")]
	public void LoopsAndBranching(string caseName, string expected)
	{
		var fileName = $"Flow/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Flow");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("TernaryInt", "100")]
	[InlineData("TernaryString", "Yes")]
	public void Ternary(string caseName, string expected)
	{
		var fileName = $"Flow/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Flow");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}
}
