namespace Cvolo.Tests.Core;

/// <summary>
/// Regression coverage for the test harness itself. Parallel tests may compile the same fixture,
/// but they must never share obj/bin directories because each compiler run can clean those outputs.
/// </summary>
public sealed class CompilerTestIsolationTests
{
	private sealed class Probe : CompilerTestBase
	{
		public string IsolationDirectory(string sourcePath) => GetOrCreateIsolatedCaseDirectory(sourcePath);
	}

	[Fact]
	public void DifferentTestInstances_GetDifferentPhysicalRootsForSameFixture()
	{
		var first = new Probe();
		var second = new Probe();

		var firstDirectory = first.IsolationDirectory("ExposeExtern/ExposeRender.cvl");
		var secondDirectory = second.IsolationDirectory("ExposeExtern/ExposeRender.cvl");

		Assert.NotEqual(firstDirectory, secondDirectory);
	}

	[Fact]
	public void SameTestInstance_ReusesRootSoNativeCompanionObjectsSurviveRebuilds()
	{
		var probe = new Probe();

		var first = probe.IsolationDirectory("UnsafeCAbi/NativeCallbackForeignGlobalFixture.cvl");
		var second = probe.IsolationDirectory("UnsafeCAbi/NativeCallbackForeignGlobalFixture.cvl");

		Assert.Equal(first, second);
	}
}
