using System.Text;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class FormattingContractsTests : CompilerTestBase
{
	private const string Category = "FormattingContracts";

	[Theory]
	[InlineData("FormatterIFormattable")]
	[InlineData("FormatterIStringable")]
	[InlineData("FormatterPrecedence")]
	[InlineData("FormatterOptionResult")]
	[InlineData("FormatterInterpolation")]
	[InlineData("FormatterNoPrintf")]
	public void Formatting_Contracts_Case_Compiles(string caseName)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
	}

	[Theory]
	[InlineData("FormatterIFormattable", "(3, 4)\n42\ncenter=(3, 4)")]
	[InlineData("FormatterIStringable", "Ada")]
	[InlineData("FormatterPrecedence", "fmt-widget")]
	[InlineData("FormatterOptionResult", "Some(42)\nNone\nOk(7)\nErr(boom)")]
	[InlineData("FormatterInterpolation", "sum=5\npi=3.500000")]
	public void Formatting_E2E_Output(string caseName, string expectedStdout)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expectedStdout, runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void Formatter_Only_Compilation_Emits_No_Printf_Reference()
	{
		const string caseName = "FormatterNoPrintf";
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var binaryPath = GetCompilationArtifactPath(fileName, "bin", "Debug", caseName + ".exe");
		Assert.True(File.Exists(binaryPath), $"Expected generated binary at '{binaryPath}'.");
		var text = Encoding.ASCII.GetString(File.ReadAllBytes(binaryPath));
		Assert.DoesNotContain("printf", text);
	}

	[Fact]
	public void NonFormattable_Value_Reports_CVL4193()
	{
		var fileName = $"{Category}/FormatterNotFormattableFail.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("CVL4193", stderr);
		Assert.Contains("Type 'Ticket' is not formattable", stderr);
	}
}
