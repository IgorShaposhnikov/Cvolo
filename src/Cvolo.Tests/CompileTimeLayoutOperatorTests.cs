using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class CompileTimeLayoutOperatorTests : CompilerTestBase
{
	[Fact]
	public void FreestandingLayoutOperators_AreAvailable()
	{
		var fileName = "Memory/LayoutOperatorsFreestanding.cvl";
		var (exitCode, stdout, stderr) = RunCompilerCheck(fileName, "--freestanding");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
	}

	[Fact]
	public void GenericLayoutQuery_FoldsAfterSubstitution()
	{
		var fileName = "Memory/LayoutOperatorsGeneric.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("LayoutOperatorsGeneric", "Memory");
		Assert.Equal(0, runCode);
		Assert.Contains("Int: 4 Long: 8", runStdout.Replace("\r\n", "\n").Trim());
	}
}
