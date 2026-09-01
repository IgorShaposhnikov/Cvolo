using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnboundSandboxTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnboundModifierBasic", "30")]
	[InlineData("NoAliasOnUnbound", "30")]
	public void ModifierExecution(string caseName, string expected)
	{
		var fileName = $"Sandbox/Unbound/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Sandbox/Unbound");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Fact]
	public void UnboundNoRefParams_Warns_But_Still_Compile_And_Run()
	{
		var fileName = "Sandbox/Unbound/UnboundNoRefParams.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.Equal(0, exitCode);
		Assert.Contains("'unbound' modifier has no effect because function has no ref/refvar parameters.", stderr);

		var (runCode, runStdout) = ExecuteBinary("UnboundNoRefParams", "Sandbox/Unbound");
		Assert.Equal(0, runCode);
		Assert.Contains("30", runStdout);
	}

	[Fact]
	public void RefEscapeToGlobal_Rejected()
	{
		var fileName = "Sandbox/Unbound/RefEscapeToGlobal.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Reference cannot escape unbound scope", stderr);
	}

	[Fact]
	public void RefEscapeUnsafeInUnbound_Rejected()
	{
		var fileName = "Sandbox/Unbound/RefEscapeUnsafeInUnbound.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Reference cannot escape unbound scope", stderr);
	}

	[Fact]
	public void RefEscapeToStructField_Rejected()
	{
		var fileName = "Sandbox/Unbound/RefEscapeToStructField.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Reference cannot escape unbound scope", stderr);
		Assert.Contains("reference field", stderr);
	}

	[Fact]
	public void RefFieldStoreLocal_Accepted()
	{
		var fileName = "Sandbox/Unbound/RefFieldStoreLocal.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("RefFieldStoreLocal", "Sandbox/Unbound");
		Assert.Equal(0, runCode);
	}

	[Fact]
	public void RefFieldMutateInSafe_Rejected()
	{
		var fileName = "Sandbox/Unbound/RefFieldMutateInSafe.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Cannot assign to reference field", stderr);
	}

	[Theory]
	[InlineData("LinkedList", "6")]
	[InlineData("DoublyLinkedList", "4")]
	[InlineData("TreeWithParent", "30")]
	[InlineData("CyclicGraph", "1")]
	[InlineData("UnboundFactory", "100")]
	public void Increment6_PositiveCorpus_Runs(string caseName, string expected)
	{
		var fileName = $"Sandbox/Increment6/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Sandbox/Increment6");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Fact]
	public void Increment6_UnboundFactory_DoesNotWarnUnboundNoRefParams()
	{
		var fileName = "Sandbox/Increment6/UnboundFactory.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("'unbound' modifier has no effect", stderr);
	}

	[Fact]
	public void Increment6_ReturnLocalRefStruct_Rejected()
	{
		var fileName = "Sandbox/Increment6/ReturnLocalRefStructFail.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Cannot return 'h' by value", stderr);
		Assert.Contains("dangling reference", stderr);
	}

	[Fact]
	public void Increment6_GlobalAssignRefFieldInSafe_Rejected()
	{
		var fileName = "Sandbox/Increment6/GlobalAssignRefFieldSafeFail.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Cannot assign to reference field", stderr);
	}

	// ---- Increment 7: unmanaged constructors & self-referential factories ----

	[Fact]
	public void Increment7_RingBufferFactory_Runs()
	{
		var fileName = "Sandbox/Increment7/RingBuffer.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("RingBuffer", "Sandbox/Increment7");
		Assert.Equal(0, runCode);
		Assert.Contains("Node value: 0", runStdout);
		Assert.Contains("Node value: 4", runStdout);
	}

	[Fact]
	public void Increment7_ReturnStackLocalRefOption_Rejected()
	{
		var fileName = "Sandbox/Increment7/ReturnStackLocalRefOptionFail.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Cannot return value", stderr);
		Assert.Contains("dangling reference", stderr);
	}
}
