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

	[Fact]
	public void LocalDeclarationInsideLoop_AllocatesStorageInEntryBlock()
	{
		var fileName = "Flow/LocalAllocaInLoop.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "-O0", "--emit-ir");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var mainIndex = stdout.IndexOf("define i32 @main()", StringComparison.Ordinal);
		Assert.True(mainIndex >= 0, "Expected generated IR to contain the main function.");
		var mainEndIndex = stdout.IndexOf("\n}", mainIndex, StringComparison.Ordinal);
		Assert.True(mainEndIndex >= 0, "Expected generated IR to contain the end of main.");

		var mainIr = stdout.Substring(mainIndex, mainEndIndex - mainIndex);
		var allocaIndex = mainIr.IndexOf("%x = alloca i32", StringComparison.Ordinal);
		var loopIndex = mainIr.IndexOf("whilebody:", StringComparison.Ordinal);
		Assert.True(allocaIndex >= 0, "Expected local 'x' to have an alloca in generated IR.");
		Assert.True(loopIndex >= 0, "Expected generated IR to contain a while body block.");
		Assert.True(allocaIndex < loopIndex, "Local stack storage must be allocated before the loop body.");

		var nextBlockIndex = mainIr.IndexOf('\n', loopIndex);
		while (nextBlockIndex >= 0)
		{
			var candidate = mainIr.IndexOf(':', nextBlockIndex + 1);
			if (candidate < 0)
			{
				nextBlockIndex = mainIr.Length;
				break;
			}

			var lineStart = mainIr.LastIndexOf('\n', candidate);
			var blockName = mainIr.Substring(lineStart + 1, candidate - lineStart - 1).Trim();
			if (blockName.Length > 0 && !blockName.StartsWith(';'))
			{
				nextBlockIndex = lineStart;
				break;
			}

			nextBlockIndex = candidate + 1;
		}

		var loopBody = mainIr.Substring(loopIndex, nextBlockIndex - loopIndex);
		Assert.DoesNotContain("alloca", loopBody);
	}
}
