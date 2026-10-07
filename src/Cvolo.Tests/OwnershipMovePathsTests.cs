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
	public void Ownership_Violations_Are_Rejected(string caseName, string expectedId)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains(expectedId, stderr);
	}
}
