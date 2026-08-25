using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class AttributesTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnsafeBodyOnFunction", "Answer: 42")]
	public void Execution(string caseName, string expected)
	{
		var fileName = $"Attributes/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Attributes");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("UnknownAttrFail", "Unknown attribute 'Bogus'.")]
	[InlineData("NoAliasOnFunctionFail", "Attribute '[NoAlias]' can only be applied inside Unbound or Unsafe contexts.")]
	[InlineData("ParamAttrContextFail", "Attribute '[NoAlias]' can only be applied inside Unbound or Unsafe contexts.")]
	[InlineData("UnsafeBodyOnStructFail", "Attribute '[UnsafeBody]' cannot be applied to struct declarations.")]
	public void Safety_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Attributes/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
