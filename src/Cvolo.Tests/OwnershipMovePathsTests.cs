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
	[InlineData("DynamicIndex_InitializeUnavailable_Accept")]
	[InlineData("DynamicIndex_NonUniform_Accept")]
	[InlineData("DynamicIndex_ReplaceLive_DropsOldExactlyOnce")]
	[InlineData("DynamicIndex_MoveOut_MarksElementUnavailable")]
	[InlineData("DynamicIndex_ReinitializeMovedElement")]
	[InlineData("DynamicIndex_ScopeCleanup_DropsOnlyLive")]
	[InlineData("DynamicIndex_ConditionalInitialization")]
	[InlineData("DynamicIndex_NoDestructorOnNeverInitialized")]
	[InlineData("DynamicIndex_NoDestructorAfterMove")]
	[InlineData("DynamicIndex_RuntimeTrace")]
	[InlineData("DynamicIndex_RuntimeTraceSameIndex")]
	[InlineData("DynamicIndex_LoopGuard_Accept")]
	[InlineData("DynamicIndex_LoopInitialization_Accept")]
	[InlineData("DynamicIndex_OptionNoneVsUnavailable")]
	[InlineData("DynamicIndex_MoveToLocal_Guarded")]
	[InlineData("DynamicIndex_MoveInReturn_Guarded")]
	[InlineData("DynamicIndex_MoveToAssignment_Guarded")]
	[InlineData("DynamicIndex_NestedProof_Accept")]
	[InlineData("DynamicIndex_AllIntegerIndexWidths")]
	[InlineData("DynamicIndex_NegativeAndOversizedIsLive")]
	[InlineData("DynamicIndex_ConstructorAssignment_KeepsFlag")]
	[InlineData("DynamicIndex_NestedOwningAggregate")]
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
	[InlineData("DynamicIndex_InitializeUnavailable_Accept", "drop=1")]
	[InlineData("DynamicIndex_NonUniform_Accept", "drop=2\ndrop=1")]
	[InlineData("DynamicIndex_ReplaceLive_DropsOldExactlyOnce", "drop=1\ndrop=9")]
	[InlineData("DynamicIndex_MoveOut_MarksElementUnavailable", "consume=7\ndrop=7")]
	[InlineData("DynamicIndex_ReinitializeMovedElement", "consume=7\ndrop=7\ndrop=8")]
	[InlineData("DynamicIndex_ScopeCleanup_DropsOnlyLive", "consume=1\ndrop=1\ndrop=2")]
	[InlineData("DynamicIndex_ConditionalInitialization", "drop=2")]
	[InlineData("DynamicIndex_NoDestructorOnNeverInitialized", "drop=5")]
	[InlineData("DynamicIndex_NoDestructorAfterMove", "consume=6\ndrop=6")]
	[InlineData("DynamicIndex_RuntimeTrace", "drop=10\nconsume=20\ndrop=20\nq0=0 q1=1 q3=1\noob=0\ndrop=40\ndrop=30")]
	[InlineData("DynamicIndex_RuntimeTraceSameIndex", "drop=10\nconsume=30\ndrop=30\nq=1\ndrop=40")]
	[InlineData("DynamicIndex_LoopGuard_Accept", "consume=7\ndrop=7")]
	[InlineData("DynamicIndex_LoopInitialization_Accept", "drop=11\ndrop=10")]
	[InlineData("DynamicIndex_OptionNoneVsUnavailable", "q0=1 q1=0")]
	[InlineData("DynamicIndex_MoveToLocal_Guarded", "q=0\nconsume=7\ndrop=7")]
	[InlineData("DynamicIndex_MoveInReturn_Guarded", "got=7\ndrop=7")]
	[InlineData("DynamicIndex_MoveToAssignment_Guarded", "drop=1\nq=0\nconsume=7\ndrop=7")]
	[InlineData("DynamicIndex_NestedProof_Accept", "inner\nconsume=7\ndrop=7")]
	[InlineData("DynamicIndex_AllIntegerIndexWidths", "b=1 s=1 u=1 l=1\ndrop=7")]
	[InlineData("DynamicIndex_NegativeAndOversizedIsLive", "neg=0 big=0")]
	[InlineData("DynamicIndex_ConstructorAssignment_KeepsFlag", "drop=7\nq=1\ndrop=9")]
	[InlineData("DynamicIndex_NestedOwningAggregate", "id=7\ndrop=7")]
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
	[InlineData("DynamicIndex_InitializeUnavailable_Accept")]
	[InlineData("DynamicIndex_NonUniform_Accept")]
	[InlineData("DynamicIndex_ReplaceLive_DropsOldExactlyOnce")]
	[InlineData("DynamicIndex_MoveOut_MarksElementUnavailable")]
	[InlineData("DynamicIndex_ReinitializeMovedElement")]
	[InlineData("DynamicIndex_ScopeCleanup_DropsOnlyLive")]
	[InlineData("DynamicIndex_ConditionalInitialization")]
	[InlineData("DynamicIndex_NoDestructorOnNeverInitialized")]
	[InlineData("DynamicIndex_RuntimeTrace")]
	[InlineData("DynamicIndex_RuntimeTraceSameIndex")]
	[InlineData("DynamicIndex_LoopInitialization_Accept")]
	[InlineData("DynamicIndex_OptionNoneVsUnavailable")]
	[InlineData("DynamicIndex_MoveToLocal_Guarded")]
	[InlineData("DynamicIndex_MoveInReturn_Guarded")]
	[InlineData("DynamicIndex_MoveToAssignment_Guarded")]
	[InlineData("DynamicIndex_NestedProof_Accept")]
	[InlineData("DynamicIndex_AllIntegerIndexWidths")]
	[InlineData("DynamicIndex_NegativeAndOversizedIsLive")]
	[InlineData("DynamicIndex_ConstructorAssignment_KeepsFlag")]
	[InlineData("DynamicIndex_NestedOwningAggregate")]
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
	[InlineData("DynamicIndex_AfterWholeMove_Reject_CVL4217", "CVL4217")]
	[InlineData("DynamicIndex_UnguardedReadFail", "CVL4221")]
	[InlineData("DynamicIndex_StaleIndexFail", "CVL4221")]
	[InlineData("DynamicIndex_FalseEdgeFail", "CVL4221")]
	[InlineData("DynamicIndex_JoinUnknownLiveFail", "CVL4221")]
	[InlineData("DynamicIndex_OverlapFail", "CVL4217")]
	[InlineData("InvalidIsLiveQueryFail", "CVL4222")]
	[InlineData("DynamicIndex_DoubleMoveUnderOneGuardFail", "CVL4221")]
	[InlineData("DynamicIndex_SideEffectingIndexFail", "CVL4221")]
	[InlineData("DynamicIndex_CallInvalidatesProofFail", "CVL4221")]
	[InlineData("DynamicIndex_UnsupportedReceiverFail", "CVL4222")]
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
