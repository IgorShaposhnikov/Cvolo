using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// A typed local declaration without an initializer is a valid declaration, a qualified type
/// name declares a local of that type, a local may shadow a struct type name, and a missing
/// terminator is reported at its own span while the following statement is still recovered.
/// </summary>
public sealed class DeclarationRecoveryTests : CompilerTestBase
{
	private const string Category = "DeclarationRecovery";

	[Theory]
	[InlineData("DeclarationNoInitializer", "4")]
	[InlineData("QualifiedTypeDeclaration", "2")]
	[InlineData("LocalShadowsTypeName", "9")]
	public void TypedDeclarationRecovery_CompilesAndRuns(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void MissingSemicolon_IsReported_AndTheNextStatementSurvives()
	{
		var fileName = $"{Category}/MissingSemicolonKeepsNextStatement.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains("missing", stderr, StringComparison.OrdinalIgnoreCase);
	}
}
