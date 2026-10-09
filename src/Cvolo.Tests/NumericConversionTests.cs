using Cvolo.Tests.Core;

namespace Cvolo.Tests;

// Regression coverage for lossless integer-to-floating implicit coercions and typed
// global initializers. Keep accepted fixtures focused on one conversion rule, and keep
// rejection fixtures checking the diagnostic family that protects the rule.
public sealed class NumericConversionTests : CompilerTestBase
{
	private const string Category = "NumericConversions";

	[Theory]
	[InlineData("GlobalFloatIntegerExact", "1\n1")]
	[InlineData("LocalReturnRuntimeMatrix", "1\n1\n1")]
	[InlineData("OverloadDoubleForInexactFloat", "2")]
	[InlineData("IntegerOverloadBeatsCoercion", "1")]
	[InlineData("CastGlobalRounds", "1\n1")]
	[InlineData("CastFromNativeIntegerGlobal", "1\n1")]
	[InlineData("UnaryPlusGlobal", "1\n1")]
	[InlineData("SignedMinimaNegative", "1\n1\n1")]
	[InlineData("OverflowWrapGlobal", "1\n1")]
	[InlineData("VectorDominanceCalc", "1")]
	public void LosslessIntegerFloatConversions_CompileAndRun(string caseName, string expectedStdout)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expectedStdout, runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("InexactFloatConstantReject", "CVL4020")]
	[InlineData("IntToFloatRuntimeReject", "CVL4065")]
	[InlineData("DoubleLiteralToFloatStillReject", "CVL1900")]
	[InlineData("AmbiguousFloatDoubleExact", "CVL1303")]
	[InlineData("AmbiguousShortFloatDouble", "CVL1303")]
	[InlineData("SignedMinimaPositiveBare", "CVL1902")]
	[InlineData("SignedMinimaNegatedPlus", "CVL1902")]
	[InlineData("CastNativeIntegerGlobalReject", "CVL4020")]
	[InlineData("OverflowWrapInexactFloatReject", "CVL4020")]
	public void InvalidIntegerFloatConversions_AreRejected(string caseName, string expectedDiagnostic)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(expectedDiagnostic, stderr);
	}
}
