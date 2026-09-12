using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class DeferTests : CompilerTestBase
{
	[Theory]
	[InlineData("Lifo", "321")]
	[InlineData("EarlyReturn", "TDFD")]
	[InlineData("Nested", "XBA")]
	[InlineData("BlockBody", "CAB")]
	[InlineData("LabeledBlockStatement", "XY")]
	[InlineData("BreakLabeledBlock", "Y")]
	[InlineData("BreakLabeledNested", "Y")]
	[InlineData("TargetedDefer", "BAC")]
	[InlineData("TargetedDeferNested", "BCA")]
	[InlineData("TargetedDeferBreak", "AB")]
	[InlineData("NestedLifo", "YZXW")]
	[InlineData("LoopDefer", "012")]
	[InlineData("SessionBreak", "AE")]
	[InlineData("CaptureByValue", "m105")]
	[InlineData("ReturnCompute", "RD")]
	public void DeferBehavior(string caseName, string expected)
	{
		var fileName = $"Defer/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Defer");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("DeferLabelNotFound", "CVL1061", "`defer Nope { ... }` refers to a label `Nope` not in scope")]
	[InlineData("BreakLabelNotFound", "CVL1061", "`break Nope;` refers to a label `Nope` not in scope")]
	[InlineData("DuplicateLabelError", "CVL1062", "Label 'L' redeclared in the same enclosing scope")]
	[InlineData("DeferReturnLeak", "CVL1063", "Control flow cannot leave a `defer` body")]
	[InlineData("DeferBreakLeak", "CVL1063", "Control flow cannot leave a `defer` body")]
	[InlineData("DeferContinueLeak", "CVL1063", "Control flow cannot leave a `defer` body")]
	[InlineData("DeferNested", "CVL1064", "`defer` cannot be nested inside another `defer`")]
	[InlineData("DeferEmpty", "CVL1065", "`defer` requires a statement or block body")]
	[InlineData("BareBreak", "CVL1066", "`break` requires a label in this version")]
	public void DeferRejections(string caseName, string expectedId, string expectedMessage)
	{
		var (exitCode, _, stderr) = RunCompiler($"Defer/{caseName}.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
		Assert.Contains(expectedMessage, stderr);
	}
}
