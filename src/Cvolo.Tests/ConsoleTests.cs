using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ConsoleTests : CompilerTestBase
{
	[Theory]
	[InlineData("ConsoleReadExplicit")]
	public void Standard_Library_Input_Compilation(string caseName)
	{
		var fileName = $"StandardLibrary/Console/{caseName}.cvl";

		// Compile-only test (avoids execution to prevent terminal hang)
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
	}

	[Fact]
	public void E2E_Console_ReadLine_Should_Accept_Input_And_Print_Successfully()
	{
		var fileName = "StandardLibrary/Console/ConsoleReadExplicit.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		// Execute the binary with "Hello Cvolo!" passed as simulated user typing!
		var (runCode, runStdout) = ExecuteBinaryWithInput("ConsoleReadExplicit", "StandardLibrary/Console", "Hello Cvolo!");

		Assert.Equal(0, runCode);
		Assert.Contains("Hello Cvolo!", runStdout.Trim());
	}
}
