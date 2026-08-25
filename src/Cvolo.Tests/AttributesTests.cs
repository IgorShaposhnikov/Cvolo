using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class AttributesTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnsafeBodyOnFunction", "Answer: 42")]
	[InlineData("UnsafeBodyOnConstructor", "Level: 9")]
	[InlineData("UnsafeBodyOnDestructor", "Closing session 7")]
	[InlineData("UnsafeBodyOnHeapAlloc", "First: 42")]
	public void Execution(string caseName, string expected)
	{
		var fileName = $"Attributes/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Attributes");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void UnsafeBody_NoEffect_Should_Warn_But_Still_Compile_And_Run()
	{
		var fileName = "Attributes/UnsafeBodyNoEffectWarn.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(0, exitCode);
		Assert.Contains("'[UnsafeBody]' attribute has no effect because function contains no unsafe operations.", stderr);

		var (runCode, runStdout) = ExecuteBinary("UnsafeBodyNoEffectWarn", "Attributes");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 42", runStdout);
	}

	[Theory]
	[InlineData("UnknownAttrFail", "Unknown attribute 'Bogus'.")]
	[InlineData("NoAliasOnFunctionFail", "Attribute '[NoAlias]' can only be applied inside Unbound or Unsafe contexts.")]
	[InlineData("ParamAttrContextFail", "Attribute '[NoAlias]' can only be applied inside Unbound or Unsafe contexts.")]
	[InlineData("UnsafeBodyOnStructFail", "Attribute '[UnsafeBody]' cannot be applied to struct declarations.")]
	[InlineData("DuplicateAttrFail", "Duplicate attribute '[UnsafeBody]'.")]
	[InlineData("SuppressWarningBadArgFail", "Attribute '[SuppressWarning]' requires exactly one string literal argument.")]
	[InlineData("SuppressWarningUnknownIdFail", "Unknown warning id 'BogusWarning'.")]
	public void Safety_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Attributes/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
