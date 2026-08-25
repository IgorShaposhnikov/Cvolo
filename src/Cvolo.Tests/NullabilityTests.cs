using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class NullabilityTests : CompilerTestBase
{
	[Theory]
	[InlineData("NullLiteralFail", "requires a pointer type")]
	public void Safety_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Nullability/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
