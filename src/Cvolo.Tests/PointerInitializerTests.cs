using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// A raw pointer field (`void*`, `int*`) may be initialized with `null` in unsafe code. Outside
/// unsafe, the use is rejected by the safety rule alone, without a spurious field-initializer type
/// mismatch. Non-pointer fields never accept `null`, and non-null integers never initialize a
/// pointer field.
/// </summary>
public sealed class PointerInitializerTests : CompilerTestBase
{
	private const string Category = "PointerInitializer";

	[Theory]
	[InlineData("StructNullUnsafeAccept", "42")]
	[InlineData("StructIntPtrNullUnsafeAccept", "7")]
	public void RawPointerFieldNull_UnsafeAccepted(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("StructNullToIntFail", "CVL4100")]
	[InlineData("StructIntToRawPtrFail", "CVL4019")]
	public void RawPointerFieldMismatch_Rejected(string caseName, string expectedId)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
	}

	[Fact]
	public void SafeNullToRawPointerField_ReportsSafetyDiagnosticWithoutTypeMismatch()
	{
		const string fileName = $"{Category}/StructNullSafeFail.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1104", stderr);
		Assert.DoesNotContain("CVL4019", stderr);
	}
}
