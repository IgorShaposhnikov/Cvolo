using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class OverloadingTests : CompilerTestBase
{
	[Theory]
	[InlineData("OverloadPrimitives", "Int: 10\nDouble: 3.140000\nString: Hello Overloading!")]
	[InlineData("OverloadReferences", "By Value: 42\nBy Reference: 42")]
	public void Function_Overloading_Resolution(string caseName, string expectedOutput)
	{
		var fileName = $"Overloading/{caseName}.cvl";

		// 1. Compile the Cvolo overload test case
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		// 2. Execute the compiled machine binary and capture stdout
		var (runCode, runStdout) = ExecuteBinary(caseName, "Overloading");

		// 3. Assert correct execution and matched overload paths
		Assert.Equal(0, runCode);

		// Normalize line endings for cross-platform validation compatibility (Windows vs Unix)
		var normalizedExpected = expectedOutput.Replace("\r\n", "\n").Trim();
		var normalizedActual = runStdout.Replace("\r\n", "\n").Trim();

		Assert.Contains(normalizedExpected, normalizedActual);
	}
}
