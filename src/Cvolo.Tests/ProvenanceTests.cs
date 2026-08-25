using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ProvenanceTests : CompilerTestBase
{
	[Theory]
	[InlineData("ParamOriginReturn")]
	[InlineData("GlobalOriginReturn")]
	[InlineData("ReassignedRefParamReturn")]
	public void LegalReturn(string caseName)
	{
		var fileName = $"Provenance/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, "", stderr, fileName);
	}

	[Theory]
	[InlineData("LocalOriginReturnFail", "Cannot return reference to local variable")]
	[InlineData("ReassignedRefLocalFail", "Cannot return reference to local variable")]
	[InlineData("RefToRefAssignLocalFail", "Cannot return reference to local variable")]
	public void DanglingRejections(string caseName, string expectedError)
	{
		var fileName = $"Provenance/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
