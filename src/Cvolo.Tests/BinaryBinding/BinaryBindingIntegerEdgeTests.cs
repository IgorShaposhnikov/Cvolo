using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Tests.BinaryBinding;

public sealed class BinaryBindingIntegerEdgeTests : BinaryBindingTestBase
{
    private const string Group = "IntegerEdgeSemantics";

    private static readonly string[] DynamicTrapCases =
    {
        "SignedDivideByZero",
        "UnsignedDivideByZero",
        "SignedRemainderByZero",
        "UnsignedRemainderByZero",
        "SignedMinDivMinusOne",
        "SignedMinRemMinusOne",
        "ShiftByWidth",
        "ShiftAboveWidth",
        "NegativeShiftCount",
        "ByteShiftByWidth",
        "CompoundDivideByZero",
        "CompoundShiftInvalid",
    };

    private static readonly string[] ValidCases =
    {
        "ValidBoundaries",
        "CompoundShiftSideEffects",
    };

    public static IEnumerable<object[]> DynamicTrapCaseData()
    {
        foreach (var caseName in DynamicTrapCases)
        {
            yield return new object[] { caseName, "-O0" };
            yield return new object[] { caseName, "-O3" };
        }
    }

    public static IEnumerable<object[]> ParityCases()
    {
        foreach (var caseName in DynamicTrapCases.Concat(ValidCases))
            yield return new object[] { caseName };
    }

    public static IEnumerable<object[]> ConstantFailCases()
    {
        yield return new object[] { "ConstantDivisionByZeroFail", "CVL4210" };
        yield return new object[] { "ConstantRemainderByZeroFail", "CVL4210" };
        yield return new object[] { "ConstantMinDivMinusOneFail", "CVL4211" };
        yield return new object[] { "ConstantShiftInvalidFail", "CVL4212" };
        yield return new object[] { "ConstantByteShiftInvalidFail", "CVL4212" };
        yield return new object[] { "ConstantNegativeShiftFail", "CVL4212" };
    }

    [Theory]
    [MemberData(nameof(DynamicTrapCaseData))]
    public void DynamicInvalidIntegerEdges_CompileAndTrapAtEveryOptimization(string caseName, string optimization)
    {
        var fileName = Fixture(Group, caseName);
        var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
        AssertCompilationSucceeded(exitCode, stdout, stderr, $"{fileName} ({optimization})");

        var (runCode, _) = ExecuteBinary(caseName, $"{Root}/{Group}");
        Assert.True(runCode != 0, $"{fileName} should have trapped at runtime at {optimization}.");
    }

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void IntegerEdgeBehavior_DoesNotChangeBetweenOptimizationLevels(string caseName)
    {
        var fileName = Fixture(Group, caseName);
        var results = new Dictionary<string, (int Code, string Out)>();

        foreach (var optimization in new[] { "-O0", "-O3" })
        {
            var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
            AssertCompilationSucceeded(exitCode, stdout, stderr, $"{fileName} ({optimization})");

            var (runCode, runStdout) = ExecuteBinary(caseName, $"{Root}/{Group}");
            results[optimization] = (runCode, runStdout);
        }

        Assert.Equal(results["-O0"].Code, results["-O3"].Code);
        Assert.Equal(results["-O0"].Out, results["-O3"].Out);
    }

    [Theory]
    [MemberData(nameof(ConstantFailCases))]
    public void ConstantInvalidIntegerEdges_AreRejectedAtCompileTime(string caseName, string expectedCode)
    {
        var fileName = Fixture(Group, caseName);
        var (exitCode, _, stderr) = RunCompiler(fileName, "-O0");

        Assert.True(exitCode != 0, $"{fileName} should be rejected at compile time.");
        Assert.Contains(expectedCode, stderr);
    }

    [Fact]
    public void DynamicIntegerEdges_EmitLlvmTrapGuardsInIr()
    {
        var fileName = Fixture(Group, "RuntimeTrapSinks");
        var (exitCode, stdout, stderr) = RunCompiler(fileName, "--llvm", "--emit-ir", "-O0");
        AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

        Assert.Contains("llvm.trap", stdout);
        Assert.Contains("icmp eq", stdout);
        Assert.Contains("icmp uge", stdout);
    }

    [Fact]
    public void SignedIntegerDivision_RecordsDivisorAndMinusOneGuards()
    {
        var (_, context) = AnalyzeProject(Fixture(Group, "Bindings"));
        AssertNoSemanticErrors(context);

        var binding = Assert.Single(
            context.ResolvedBinaryExpressions.Values,
            candidate => candidate.Operator == "/"
                && candidate.LeftType == TypeSymbol.Int
                && candidate.RightType == TypeSymbol.Int);

        Assert.True(binding.IntegerEdgeCheckDivisorNonZero);
        Assert.True(binding.IntegerEdgeCheckSignedMinMinusOne);
        Assert.Equal(32, binding.IntegerEdgeValueBitWidth);
        Assert.True(binding.HasIntegerEdgeGuards);
    }

    [Fact]
    public void UnsignedIntegerDivision_RecordsDivisorButNoMinusOneGuard()
    {
        var (_, context) = AnalyzeProject(Fixture(Group, "Bindings"));
        AssertNoSemanticErrors(context);

        var binding = Assert.Single(
            context.ResolvedBinaryExpressions.Values,
            candidate => candidate.Operator == "/"
                && candidate.LeftType == TypeSymbol.UInt
                && candidate.RightType == TypeSymbol.UInt);

        Assert.True(binding.IntegerEdgeCheckDivisorNonZero);
        Assert.False(binding.IntegerEdgeCheckSignedMinMinusOne);
        Assert.Equal(32, binding.IntegerEdgeValueBitWidth);
        Assert.True(binding.HasIntegerEdgeGuards);
    }

    [Fact]
    public void MixedSignednessDivision_LowersViaUnsignedSemanticsSoNoMinusOneGuard()
    {
        var (_, context) = AnalyzeProject(Fixture(Group, "Bindings"));
        AssertNoSemanticErrors(context);

        var longDiv = Assert.Single(
            context.ResolvedBinaryExpressions.Values,
            candidate => candidate.Operator == "/"
                && candidate.LeftType == TypeSymbol.Long
                && candidate.RightType == TypeSymbol.UInt);
        Assert.Equal(TypeSymbol.Long, longDiv.OperationType);
        Assert.True(longDiv.IntegerEdgeCheckDivisorNonZero);
        Assert.False(longDiv.IntegerEdgeCheckSignedMinMinusOne);
        Assert.Equal(64, longDiv.IntegerEdgeValueBitWidth);

        var smallDiv = Assert.Single(
            context.ResolvedBinaryExpressions.Values,
            candidate => candidate.Operator == "/"
                && candidate.LeftType == TypeSymbol.Byte
                && candidate.RightType == TypeSymbol.UShort);
        Assert.Equal(TypeSymbol.UShort, smallDiv.OperationType);
        Assert.False(smallDiv.IntegerEdgeCheckSignedMinMinusOne);
        Assert.Equal(16, smallDiv.IntegerEdgeValueBitWidth);
    }

    [Fact]
    public void ShiftWidth_IsDerivedFromLeftOperandSemanticWidth()
    {
        var (_, context) = AnalyzeProject(Fixture(Group, "Bindings"));
        AssertNoSemanticErrors(context);

        var byteShift = Assert.Single(
            context.ResolvedBinaryExpressions.Values,
            candidate => candidate.Operator == "<<"
                && candidate.LeftType == TypeSymbol.Byte
                && candidate.RightType == TypeSymbol.Int);
        Assert.True(byteShift.IntegerEdgeCheckShiftCountDomain);
        Assert.Equal(8, byteShift.IntegerEdgeValueBitWidth);

        var longShift = Assert.Single(
            context.ResolvedBinaryExpressions.Values,
            candidate => candidate.Operator == ">>"
                && candidate.LeftType == TypeSymbol.Long
                && candidate.RightType == TypeSymbol.Long);
        Assert.True(longShift.IntegerEdgeCheckShiftCountDomain);
        Assert.Equal(64, longShift.IntegerEdgeValueBitWidth);

        var uintLogicalShift = Assert.Single(
            context.ResolvedBinaryExpressions.Values,
            candidate => candidate.Operator == ">>>"
                && candidate.LeftType == TypeSymbol.UInt
                && candidate.RightType == TypeSymbol.Int);
        Assert.True(uintLogicalShift.IntegerEdgeCheckShiftCountDomain);
        Assert.Equal(32, uintLogicalShift.IntegerEdgeValueBitWidth);
        Assert.Equal(TypeSymbol.UInt, uintLogicalShift.LeftConversion!.ValueType);
    }

    [Fact]
    public void UserDefinedOperators_AreExemptFromIntegerEdgeGuards()
    {
        var (_, context) = AnalyzeProject(Fixture(Group, "Bindings"));
        AssertNoSemanticErrors(context);

        var binding = Assert.Single(
            context.ResolvedBinaryExpressions.Values,
            candidate => candidate.IsUserDefined);
        Assert.False(binding.HasIntegerEdgeGuards);
    }
}