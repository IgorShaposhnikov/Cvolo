using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class LiteralsTests : CompilerTestBase
{
	private const string Category = "Literals";

	[Theory]
	[InlineData("HexBinary", "65280\n10\n65535\n3735928559\n")]
	[InlineData("IntegerSuffixes", "4000000000\n9000000000\n18000000000\n18000000001\n42\n9999999999\n")]
	[InlineData("DigitSeparators", "1000000\n1000\n1000.500000\n65535\n")]
	[InlineData("FloatSuffixes", "1.500000\n2.000000\n3.000000\n314.000000\n0.006280\n1.000000\n")]
	[InlineData("FloatNegation", "-0.250000\n-0.500000\n-0.500000\n")]
	[InlineData("CharEscapes", "10\n9\n0\n92\n39\n65\n")]
	public void Literals_CompileAndRun(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("FloatToDoubleLiteralFail", "CVL1900")]
	[InlineData("UnknownFloatSuffixFail", "CVL1901")]
	[InlineData("IntegerTooLargeFail", "CVL1902")]
	[InlineData("InvalidIntegerSuffixFail", "CVL1903")]
	[InlineData("TrailingSeparatorFail", "CVL1904")]
	[InlineData("LeadingUnderscoreNumberFail", "CVL1904")]
	[InlineData("EmptyCharLiteralFail", "CVL1905")]
	[InlineData("UnknownEscapeFail", "CVL1906")]
	[InlineData("MultiCharLiteralFail", "CVL1907")]
	[InlineData("HexEscapeRangeFail", "CVL1908")]
	[InlineData("NullOutsideUnsafeFail", "CVL1104")]
	[InlineData("NullToRefFail", "CVL1104")]
	public void Literals_BadInputRejected(string caseName, string expectedId)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
	}
}
