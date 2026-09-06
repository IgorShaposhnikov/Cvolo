using Cvolo.Tests.Core;
using Xunit;

namespace Cvolo.Tests;

public class TypeAliasesTests : CompilerTestBase
{
	[Theory]
	[InlineData("TypeAliasBasic", "43\n7")]
	[InlineData("TypeAliasGeneric", "52\n1")]
	[InlineData("TypeAliasChain", "126")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"TypeAliases/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "TypeAliases");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("TypeAliasUnknownTarget")]
	[InlineData("TypeAliasCycle")]
	[InlineData("TypeAliasCycleSelf")]
	[InlineData("TypeAliasWhereConstraint")]
	[InlineData("TypeAliasDuplicate")]
	[InlineData("TypeAliasConflict")]
	public void Rejections(string caseName)
	{
		var fileName = $"TypeAliases/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.Equal(1, exitCode);
	}
}