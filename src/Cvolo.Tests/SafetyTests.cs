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

	/// <summary>
	/// INC A: executable positive cases verify that ordinary writable access,
	/// receiver calls, and permitted unbound aliases still behave as expected.
	/// </summary>
	[Theory]
	[InlineData("UnboundAliasTracking", "42")]
	[InlineData("ReceiverBorrowLastUse", "42")]
	[InlineData("TypedFieldAndArrayMutation", "42")]
	public void SafetyHardening_PositiveExecution(string caseName, string expected)
	{
		var fileName = $"Safety/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Safety");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	/// <summary>
	/// INC A / A5: writes reached through read-only ref/val paths are rejected,
	/// including nested fields and fixed-array elements.
	/// </summary>
	[Theory]
	[InlineData("RefReadonlyFieldFail")]
	[InlineData("ValReadonlyFieldFail")]
	[InlineData("RefReadonlyFixedElementFail")]
	public void ReadOnlyAccessPathsRejectWrites(string caseName)
	{
		var fileName = $"Safety/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains("Cannot assign through a read-only reference or immutable access path", stderr);
	}

	/// <summary>
	/// INC A / A5: guarding ref/val paths must not block legitimate refvar writes.
	/// </summary>
	[Fact]
	public void RefvarAccessPathRemainsWritable()
	{
		const string fileName = "Safety/RefvarWritableField.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("RefvarWritableField", "Safety");
		Assert.Equal(0, runCode);
		Assert.Contains("42", runStdout);
	}

	/// <summary>
	/// INC A / A4: borrowed storage is still needed after an inner block or
	/// on the next loop iteration; owner replacement must remain rejected.
	/// </summary>
	[Theory]
	[InlineData("BorrowOuterBlockStillLiveFail")]
	[InlineData("BorrowOuterLoopBackedgeFail")]
	public void NestedNllMustNotPrematurelyReleaseOuterReference(string caseName)
	{
		var fileName = $"Safety/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains("while a field borrow is still active", stderr);
	}

	/// <summary>
	/// INC A / A1: a moved resource may not be accessed through a bound method
	/// receiver even when the call has no explicit argument expression.
	/// </summary>
	[Fact]
	public void MovedInstanceMethodReceiverIsRejected()
	{
		const string fileName = "Safety/MovedMethodReceiverFail.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains("Use of moved variable 'owner'", stderr);
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
