using Cvolo.Tests.Shared;

namespace Cvolo.Tests.Base;

/// <summary>
/// End-to-end coverage for Base memory/layout contracts. The fixture compares the Base
/// <c>Layout.FromType&lt;T&gt;</c> facade with the compiler's layout operators in a freestanding build.
/// </summary>
public sealed class MemoryTests : CompilerTestBase
{
	[Fact]
	public void Layout_FromType_MatchesCompileTimeLayoutOperators()
	{
		const string fixture = "Memory/LayoutFromType.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fixture, "--freestanding");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fixture);

		var (runCode, _) = ExecuteBinary("LayoutFromType", "Memory");
		Assert.Equal(0, runCode);
	}
}
