using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnionsTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnionDecl", "Integer: 42")]
	[InlineData("UnionFieldAccess", "Ok: 42")]
	[InlineData("UnionExtension", "Some: 100, IsSome: 1\nEmpty option created.")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"Unions/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Unions");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("UnionDuplicateFieldFail", "Duplicate field 'Active'")]
	[InlineData("UnionFieldAccessFail", "does not contain variant 'Missing'")]
	public void Semantic_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Unions/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
