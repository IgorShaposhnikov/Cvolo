using Cvolo.Tests.Core;

namespace Cvolo.Tests;

// Regression coverage for canonical compile-time constant expressions, primarily
// static-array extents. These fixtures lock in that extents use ordinary
// expression precedence, parentheses, `%`, layout operators and enum metadata,
// and that the evaluated extent (not the source spelling) defines array type
// identity. Add a case here whenever the constant evaluator changes.
public sealed class ConstantExpressionTests : CompilerTestBase
{
	private const string Category = "ConstantExpressions";

	[Theory]
	[InlineData("CanonicalPrecedence", "global=14\narray-bytes=56")]
	[InlineData("LayoutTermPrecedence", "bytes=11")]
	[InlineData("ParenthesizedPrecedence", "bytes=20")]
	[InlineData("ModuloConstant", "bytes=3")]
	[InlineData("UnaryAndIntermediate", "a=8\nb=28")]
	[InlineData("EnumMetadataComposes", "a=4")]
	[InlineData("EquivalentSpellings", "ok=56")]
	public void ExtentUsesOrdinaryExpressionSemantics(string caseName, string expectedStdout)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expectedStdout, runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("CanonicalPrecedence")]
	[InlineData("LayoutTermPrecedence")]
	[InlineData("ParenthesizedPrecedence")]
	[InlineData("ModuloConstant")]
	[InlineData("UnaryAndIntermediate")]
	[InlineData("EnumMetadataComposes")]
	[InlineData("EquivalentSpellings")]
	public void Extent_DoesNotChangeBetweenOptimizationLevels(string caseName)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var results = new Dictionary<string, (int Code, string Stdout)>();

		foreach (var optimization in new[] { "-O0", "-O3" })
		{
			var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
			AssertCompilationSucceeded(exitCode, stdout, stderr, $"{fileName} ({optimization})");

			var (runCode, runStdout) = ExecuteBinary(caseName, Category);
			results[optimization] = (runCode, runStdout.Replace("\r\n", "\n").Trim());
		}

		Assert.Equal(results["-O0"].Code, results["-O3"].Code);
		Assert.Equal(results["-O0"].Stdout, results["-O3"].Stdout);
	}

	[Theory]
	[InlineData("DivisionByZeroReject")]
	[InlineData("RemainderByZeroReject")]
	[InlineData("NegativeExtentReject")]
	[InlineData("NonConstantVariableReject")]
	public void InvalidExtent_IsRejected(string caseName)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("CVL4023", stderr);
	}
}
