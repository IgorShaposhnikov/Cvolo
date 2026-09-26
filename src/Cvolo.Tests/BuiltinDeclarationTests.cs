using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public class BuiltinDeclarationTests : CompilerTestBase
{
	[Fact]
	public void BuiltinOutsideBase_IsRejected()
	{
		var (exitCode, _, stderr) = RunCompilerCheck("Inc11/BuiltinSpoof.cvl");
		Assert.NotEqual(0, exitCode);
		Assert.Contains("CVL2112", stderr);
	}

	[Fact]
	public void OrdinaryProgram_CompilesWithoutBuiltinManifestErrors()
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck("Inc11/Ordinary.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Inc11/Ordinary.cvl");
	}
}
