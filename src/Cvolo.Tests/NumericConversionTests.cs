using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class NumericConversionTests : CompilerTestBase
{
    private const string Category = "NumericConversions";

    [Fact]
    public void AllowedNumericConversions_CompileAndRun()
    {
        var (exitCode, stdout, stderr) = RunCompiler($"{Category}/AllowedImplicit.cvl");
        AssertCompilationSucceeded(exitCode, stdout, stderr, "AllowedImplicit.cvl");
        var (runCode, runStdout) = ExecuteBinary("AllowedImplicit", Category);
        Assert.Equal(0, runCode);
        Assert.Equal("200\n-5\n4000000000\n300\n1\n44", runStdout.Replace("\r\n", "\n").Trim());
    }

    [Theory]
    [InlineData("RejectIntToByte", "CVL1911")]
    [InlineData("RejectLongToInt", "CVL1911")]
    [InlineData("RejectSignedness", "CVL1911")]
    [InlineData("RejectDoubleToFloat", "CVL1911")]
    [InlineData("RejectAssignment", "CVL1911")]
    [InlineData("RejectUnsafe", "CVL1911")]
    [InlineData("RejectLiteral", "CVL1902")]
    public void UnsafeNumericConversions_AreRejected(string caseName, string expectedId)
    {
        var (exitCode, _, stderr) = RunCompiler($"{Category}/{caseName}.cvl");
        Assert.Equal(1, exitCode);
        Assert.Contains(expectedId, stderr);
    }

    [Theory]
    [InlineData("RejectCall")]
    [InlineData("RejectReturn")]
    public void NarrowingInCallsAndReturns_IsRejected(string caseName)
    {
        var (exitCode, _, stderr) = RunCompiler($"{Category}/{caseName}.cvl");
        Assert.Equal(1, exitCode);
        Assert.Contains("CVL", stderr);
    }
}
