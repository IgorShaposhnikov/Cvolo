using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public class BuiltinDeclarationTests : CompilerTestBase
{
	[Fact]
	public void BuiltinOutsideBase_IsRejected()
	{
		var (exitCode, _, stderr) = RunCompilerCheck("BuiltinDeclarations/BuiltinSpoof.cvl");
		Assert.NotEqual(0, exitCode);
		Assert.Contains("CVL2112", stderr);
	}

	[Fact]
	public void OrdinaryProgram_CompilesWithoutBuiltinManifestErrors()
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck("BuiltinDeclarations/Ordinary.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "BuiltinDeclarations/Ordinary.cvl");
	}
}
