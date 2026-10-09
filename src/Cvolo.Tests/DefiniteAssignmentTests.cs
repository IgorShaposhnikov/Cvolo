using Cvolo.Tests.Core;

namespace Cvolo.Tests;

// Regression coverage for the definite-assignment flow analyzer.
// Add cases here when DA rules change, and keep each fixture focused on one
// control-flow/projection behavior so failures point to the broken rule.
public sealed class DefiniteAssignmentTests : CompilerTestBase
{
	private const string Category = "DefiniteAssignment";

	[Theory]
	[InlineData("IfThenOnlyReject")]
	[InlineData("IfElseMissingPathReject")]
	[InlineData("PartialStructInitializationReject")]
	[InlineData("RefvarDoesNotInitializeReject")]
	[InlineData("LazyAndConditionReject")]
	public void ReadsWithoutDefiniteInitialization_AreRejected(string caseName)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("CVL4139", stderr);
	}

	[Theory]
	[InlineData("IfElseAllPaths", "11\n22")]
	[InlineData("SwitchAllPaths", "10\n20")]
	[InlineData("EarlyReturnJoin", "10\n20")]
	[InlineData("LazyAndBothBranches", "7\n3")]
	[InlineData("TernaryAllPaths", "11\n22")]
	[InlineData("StructFieldsAssigned", "33")]
	public void DefinitelyInitialized_CompilesAndRuns(string caseName, string expectedStdout)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expectedStdout, runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("IfElseAllPaths")]
	[InlineData("SwitchAllPaths")]
	[InlineData("EarlyReturnJoin")]
	[InlineData("LazyAndBothBranches")]
	[InlineData("TernaryAllPaths")]
	[InlineData("StructFieldsAssigned")]
	public void DefiniteAssignment_DoesNotChangeBetweenOptimizationLevels(string caseName)
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
}
