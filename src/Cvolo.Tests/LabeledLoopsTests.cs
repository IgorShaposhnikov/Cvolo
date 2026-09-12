using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class LabeledLoopsTests : CompilerTestBase
{
	[Theory]
	[InlineData("SimpleBreak", "012!")]
	[InlineData("SimpleContinue", "13")]
	[InlineData("LabeledBreak", "00!")]
	[InlineData("LabeledContinue", "000110112021")]
	[InlineData("DeferSplice", "0IIO0IIO")]
	[InlineData("DeferredTargetValid", "0O1O")]
	[InlineData("BreakInSwitch", "X.S.S.X.C.C.done")]
	public void LabeledLoopBehavior(string caseName, string expected)
	{
		var fileName = $"LabeledLoops/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "LabeledLoops");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("BreakOutsideLoop", "CVL1066", "`break` requires a label in this version")]
	[InlineData("ContinueOutsideLoop", "CVL1070", "can only be executed inside an active loop or switch-case body context")]
	[InlineData("ContinueInSwitchFail", "CVL1070", "can only be executed inside an active loop or switch-case body context")]
	[InlineData("UnresolvedLabel", "CVL1061", "`break outer;` refers to a label `outer` not in scope")]
	[InlineData("DuplicateLabel", "CVL1062", "Label 'outer' redeclared in the same enclosing scope")]
	public void LabeledLoopRejections(string caseName, string expectedId, string expectedMessage)
	{
		var (exitCode, _, stderr) = RunCompiler($"LabeledLoops/{caseName}.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
		Assert.Contains(expectedMessage, stderr);
	}

	[Fact]
	public void ParenthesizedBreak_Is_SyntaxError()
	{
		var (exitCode, _, stderr) = RunCompiler("LabeledLoops/ParenthesizedBreakFail.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains("break", stderr);
	}
}
