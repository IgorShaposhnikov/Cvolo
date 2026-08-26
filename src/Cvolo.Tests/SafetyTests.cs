using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class SafetyTests : CompilerTestBase
{
	[Theory]
	[InlineData("BorrowLockMoveFail", "Cannot move 'r' while a field borrow is still active")]
	[InlineData("BorrowLockReassignFail", "Cannot reassign 'r' while a field borrow is still active")]
	[InlineData("BorrowLockReturnFail", "Cannot move 'r' while a field borrow is still active")]
	[InlineData("BorrowLockMultiElementFail", "already borrowed; cannot borrow multiple elements")]
	public void BorrowLockRejections(string caseName, string expectedError)
	{
		var fileName = $"Safety/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("BorrowLockSequential")]
	[InlineData("BorrowLockStructFieldOK")]
	[InlineData("LifetimeConstrainedReturnOK")]
	[InlineData("GlobalLifetimeAssignOK")]
	public void BorrowLockLegal(string caseName)
	{
		var fileName = $"Safety/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, "", stderr, fileName);
	}

	[Theory]
	[InlineData("LifetimeConstrainedReturnFail", "Cannot return 'w' by value: reference field 'p' targets local variable")]
	[InlineData("GlobalLifetimeAssignFail", "only global-origin references may be stored in globals")]
	[InlineData("CycleCutSelfRef")]
	[InlineData("CycleCutTransitive")]
	public void LifetimeConstrained(string caseName, string? expectedError = null)
	{
		var fileName = $"Safety/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		if (expectedError != null)
		{
			Assert.Equal(1, exitCode);
			Assert.Contains(expectedError, stderr);
		}
		else
		{
			AssertCompilationSucceeded(exitCode, "", stderr, fileName);
		}
	}

	[Fact]
	public void RefvarReassignCodegen()
	{
		var (exitCode, _, stderr) = RunCompiler("Safety/RefvarReassignCodegen.cvl");
		AssertCompilationSucceeded(exitCode, "", stderr, "Safety/RefvarReassignCodegen.cvl");

		var (runCode, runStdout) = ExecuteBinary("RefvarReassignCodegen", "Safety");
		Assert.Equal(0, runCode);
		Assert.Contains("30", runStdout);
		Assert.Contains("70", runStdout);
	}

	[Fact]
	public void RefvarPointerChase()
	{
		var (exitCode, _, stderr) = RunCompiler("Safety/RefvarPointerChase.cvl");
		AssertCompilationSucceeded(exitCode, "", stderr, "Safety/RefvarPointerChase.cvl");

		var (runCode, runStdout) = ExecuteBinary("RefvarPointerChase", "Safety");
		Assert.Equal(0, runCode);
		Assert.Contains("10", runStdout);
		Assert.Contains("20", runStdout);
		Assert.Contains("30", runStdout);
	}
}
