using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class TryCatchTests : CompilerTestBase
{
	[Theory]
	[InlineData("InlineLiteralCatch", "3\n0")]
	[InlineData("ArrowCatch", "42\n100")]
	[InlineData("ReturnSeamCatch", "5\n-1")]
	[InlineData("TrySuccess", "2")]
	[InlineData("TryCatchFirstClause", "333")]
	[InlineData("TryCatchCascade", "444")]
	[InlineData("TryUnmatchedError", "5\n0")]
	[InlineData("MixedErrorReceivers", "1")]
	[InlineData("ErrorAttribute", "5\n7")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"TryCatch/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "TryCatch");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("DuplicateCatchClause", "Duplicate `catch` block clause detected")]
	[InlineData("UnreachableClause", "is unreachable")]
	[InlineData("UncoveredErrorType", "has no matching `catch` clause")]
	[InlineData("LambdaMissingReturn", "must end with a `return` statement")]
	[InlineData("PatternNotErrorAttribute", "not marked `[Error]`")]
	[InlineData("ValuePatternOnStruct", "to be an `enum`")]
	[InlineData("NestedCatch", "requires a valid left-hand expression")]
	[InlineData("UntypedDeclCatch", "requires a valid left-hand expression")]
	public void Semantic_Rejections(string caseName, string expectedError)
	{
		var fileName = $"TryCatch/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
