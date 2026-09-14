using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class GlobalsTests : CompilerTestBase
{
	[Theory]
	[InlineData("GlobalCounter", "Count: 3")]
	[InlineData("GlobalStruct", "X=100, Y=200")]
	[InlineData("GlobalZeroInit", "Unset: 0")]
	public void Execution(string caseName, string expected)
	{
		var fileName = $"Globals/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Globals");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("GlobalNonConstFail", "must be initialized with a compile-time constant")]
	[InlineData("GlobalConstWriteFail", "Cannot assign to immutable variable 'ReadOnly'")]
	public void Safety_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Globals/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("QualifiedAccess", "Bare: 42\nQualified: 42")]
	[InlineData("MathQualifiedGlobals", "IntMax: 2147483647\nUIntMax: 4294967295\nLongMin: -9223372036854775808")]
	public void FullyQualified_Execution(string caseName, string expected)
	{
		var fileName = $"Globals/FQ/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Globals/FQ");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("SameNameDifferentNamespaces", "Alpha: 1\nBeta: 2")]
	[InlineData("SameNameAcrossPackageBoundary", "Root: 10\nQualified: 20")]
	public void SameShortName_QualifiedAccess(string caseName, string expected)
	{
		var folder = $"Globals/FQ/{caseName}";
		var (exitCode, stdout, stderr) = RunCompiler(folder);
		AssertCompilationSucceeded(exitCode, stdout, stderr, folder);

		var (runCode, runStdout) = ExecuteBinary(caseName, folder);
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void AmbiguousUnqualifiedReference_Rejected()
	{
		const string folder = "Globals/FQ/AmbiguousUnqualifiedReference";
		var (exitCode, stdout, stderr) = RunCompiler(folder);

		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1077", stderr);
		Assert.Contains("Reference to 'Value' is ambiguous between 'Alpha.Value' and 'Beta.Value'.", stderr);
	}

	[Fact]
	public void BindingContext_TracksSameShortNameAcrossNamespaces()
	{
		var (_, context) = AnalyzeProject("Globals/FQ/SameNameDifferentNamespaces");

		Assert.True(context.GlobalsByQualifiedName.ContainsKey("Alpha.Value"));
		Assert.True(context.GlobalsByQualifiedName.ContainsKey("Beta.Value"));

		var bare = context.ResolveGlobalReference("Value", out var ambiguousCandidates);
		Assert.Null(bare);
		Assert.NotNull(ambiguousCandidates);
		Assert.Contains("Alpha.Value", ambiguousCandidates);
		Assert.Contains("Beta.Value", ambiguousCandidates);
	}
}
