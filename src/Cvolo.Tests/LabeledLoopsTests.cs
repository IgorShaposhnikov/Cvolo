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
	[InlineData("BreakOutsideLoop", "CVL1070", "can only be executed inside an active loop or switch-case body context")]
	[InlineData("ContinueOutsideLoop", "CVL1070", "can only be executed inside an active loop or switch-case body context")]
	[InlineData("ContinueInSwitchFail", "CVL1070", "can only be executed inside an active loop or switch-case body context")]
	[InlineData("UnresolvedLabel", "CVL1063", "Labeled branch target 'outer' could not be resolved")]
	[InlineData("DuplicateLabel", "CVL1062", "Loop iteration label identifier 'outer' is redeclared")]
	[InlineData("DeferBarrierFail", "CVL1071", "violates the cross-iteration loop barrier")]
	[InlineData("DeferFlatLabelFail", "CVL1067", "requires a trailing colon identifier after label 'outer'")]
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
