using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class OwnershipMovePathsTests : CompilerTestBase
{
	private const string Category = "OwnershipMovePaths";

	[Theory]
	[InlineData("LiveLocalReassignment")]
	[InlineData("LiveFieldReassignment")]
	[InlineData("PartialMoveField")]
	[InlineData("SiblingFieldMove")]
	[InlineData("DeclMoveField")]
	[InlineData("ConditionalMoveThenReplace")]
	[InlineData("ConditionalMoveTaken")]
	[InlineData("ConditionalInitThenReplace")]
	[InlineData("ConditionalInitTaken")]
	[InlineData("UnionVariantChange")]
	[InlineData("FixedArray_PartialInit_ScopeExit_DropsOnlyLive")]
	[InlineData("FixedArray_AllFixedElementsInitialized_ScopeExit_DropsEachOnce")]
	[InlineData("FixedArray_ConditionalFixedInit_DropsOnlyTakenElement")]
	[InlineData("FixedArray_FixedMove_ScopeExit_SkipsMovedElement")]
	[InlineData("DynamicIndex_AllLive_Replacement_DropsOldSelectedOnce")]
	[InlineData("DynamicIndex_AllLive_Replacement_FinalCleanupExactlyOnce")]
	[InlineData("RefvarReplacement")]
	[InlineData("RefvarFieldReplacement")]
	[InlineData("RefvarRebind")]
	public void Ownership_Replacement_Cases_Compile(string caseName)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
	}

	[Theory]
	[InlineData("LiveLocalReassignment", "drop=1\nafter-reassign\ndrop=2")]
	[InlineData("LiveFieldReassignment", "drop=1\nafter-field-reassign\ndrop=3\ndrop=2")]
	[InlineData("PartialMoveField", "drop=1\nother=2\ndrop=2")]
	[InlineData("SiblingFieldMove", "drop=1\nafter\ndrop=2")]
	[InlineData("DeclMoveField", "x=1 other=2\ndrop=2\ndrop=1")]
	[InlineData("ConditionalMoveThenReplace", "drop=1\nend\ndrop=2")]
	[InlineData("ConditionalMoveTaken", "drop=1\nend\ndrop=2")]
	[InlineData("ConditionalInitThenReplace", "end\ndrop=1")]
	[InlineData("ConditionalInitTaken", "drop=1\nend\ndrop=2")]
	[InlineData("UnionVariantChange", "drop=1\nend")]
	[InlineData("FixedArray_PartialInit_ScopeExit_DropsOnlyLive", "end\ndrop=1")]
	[InlineData("FixedArray_AllFixedElementsInitialized_ScopeExit_DropsEachOnce", "end\ndrop=2\ndrop=1")]
	[InlineData("FixedArray_ConditionalFixedInit_DropsOnlyTakenElement", "end\ndrop=1")]
	[InlineData("FixedArray_FixedMove_ScopeExit_SkipsMovedElement", "drop=2\nend\ndrop=1")]
	[InlineData("DynamicIndex_AllLive_Replacement_DropsOldSelectedOnce", "drop=2\ndrop=9\ndrop=1")]
	[InlineData("DynamicIndex_AllLive_Replacement_FinalCleanupExactlyOnce", "drop=2\nend\ndrop=9\ndrop=1")]
	[InlineData("RefvarReplacement", "drop=1\nend\ndrop=2")]
	[InlineData("RefvarFieldReplacement", "drop=1\nend\ndrop=3\ndrop=2")]
	[InlineData("RefvarRebind", "drop=2\nend\ndrop=1\ndrop=3")]
	public void Ownership_Replacement_E2E_Output(string caseName, string expectedStdout)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expectedStdout, runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("LiveLocalReassignment")]
	[InlineData("LiveFieldReassignment")]
	[InlineData("PartialMoveField")]
	[InlineData("ConditionalMoveThenReplace")]
	[InlineData("ConditionalInitThenReplace")]
	[InlineData("UnionVariantChange")]
	[InlineData("FixedArray_PartialInit_ScopeExit_DropsOnlyLive")]
	[InlineData("FixedArray_AllFixedElementsInitialized_ScopeExit_DropsEachOnce")]
	[InlineData("DynamicIndex_AllLive_Replacement_FinalCleanupExactlyOnce")]
	[InlineData("RefvarReplacement")]
	[InlineData("RefvarRebind")]
	public void Ownership_Replacement_Does_Not_Change_Between_Optimization_Levels(string caseName)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var results = new Dictionary<string, (int Code, string Stdout)>();

		foreach (var optimization in new[] { "-O0", "-O3" })
		{
			var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
			AssertCompilationSucceeded(exitCode, stdout, stderr, $"{fileName} ({optimization})");

			var (runCode, runStdout) = ExecuteBinary(caseName, Category);
			results[optimization] = (runCode, runStdout.Replace("\r\n", "\n").Trim());
		}

		Assert.Equal(results["-O0"].Code, results["-O3"].Code);
		Assert.Equal(results["-O0"].Stdout, results["-O3"].Stdout);
	}

	[Theory]
	[InlineData("SelfMoveFail", "CVL4213")]
	[InlineData("OverlapMoveFail", "CVL4214")]
	[InlineData("DestructorAncestorMoveFail", "CVL4215")]
	[InlineData("DynamicIndexMoveFail", "CVL4217")]
	[InlineData("DynamicIndex_NonUniform_Reject_CVL4217", "CVL4217")]
	[InlineData("DynamicIndex_AllUnavailable_Reject_CVL4217", "CVL4217")]
	[InlineData("DynamicIndex_AfterWholeMove_Reject_CVL4217", "CVL4217")]
	[InlineData("RawPointerAssignmentFail", "CVL4218")]
	[InlineData("RawPointerExtractionFail", "CVL4218")]
	[InlineData("RawPointerCallFail", "CVL4218")]
	public void Ownership_Violations_Are_Rejected(string caseName, string expectedId)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains(expectedId, stderr);
	}

	[Fact]
	public void Move_Diagnostic_Precedence_Suppresses_Uninitialized_Read()
	{
		var fileName = $"{Category}/MoveDiagnosticPrecedence.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("CVL4138", stderr);
		Assert.DoesNotContain("CVL4139", stderr);
	}
}
