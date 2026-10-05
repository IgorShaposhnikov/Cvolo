using Cvolo.Tests.Shared;

namespace Cvolo.Tests.Base;

/// <summary>
/// Verifies the boundary of the Base-only compilation mode itself: Base programs are accepted,
/// while importing System is rejected before a System module can leak into the test suite.
/// </summary>
public sealed class FreestandingTests : CompilerTestBase
{
	[Fact]
	public void SystemImport_IsRejectedInBaseOnlyCompilation()
	{
		const string fixture = "Freestanding/SystemImportFail.cvl";
		var (exitCode, _, stderr) = RunCompilerCheck(fixture, "--freestanding");

		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1097", stderr);
	}
}
