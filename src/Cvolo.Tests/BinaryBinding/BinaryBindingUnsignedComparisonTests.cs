using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Tests.BinaryBinding;

/// <summary>
/// Verifies INC "Unsigned Integer Comparison Lowering": relational comparisons on builtin
/// integer types select LLVM signed/unsigned predicates from the canonical common semantic
/// type recorded by the binder (never from operand bit widths, never from "either operand
/// is unsigned"), and global constant folding agrees with the same runtime semantics.
/// </summary>
public sealed class BinaryBindingUnsignedComparisonTests : BinaryBindingTestBase
{
    public static IEnumerable<object[]> OptimizationLevels()
    {
        yield return new object[] { "-O0" };
        yield return new object[] { "-O3" };
    }

    [Theory]
    [MemberData(nameof(OptimizationLevels))]
    public void UnsignedIntegerComparisons_AreStableAcrossOptimizationLevels(string optimization)
    {
        var fileName = Fixture("UnsignedComparison", "RuntimeUnsigned");
        var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
        AssertCompilationSucceeded(exitCode, stdout, stderr, $"{fileName} ({optimization})");

        var (runCode, _) = ExecuteBinary("RuntimeUnsigned", $"{Root}/UnsignedComparison");
        Assert.True(runCode == 0, $"{fileName} exited with {runCode} at {optimization}.");
    }

    [Fact]
    public void GlobalComparisonFolding_MatchesRuntimeSemantics()
    {
        var fileName = Fixture("UnsignedComparison", "GlobalComparisonFold");
        var (exitCode, stdout, stderr) = RunCompiler(fileName);
        AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

        var (runCode, _) = ExecuteBinary("GlobalComparisonFold", $"{Root}/UnsignedComparison");
        Assert.True(runCode == 0, $"{fileName} exited with {runCode}.");
    }

    [Fact]
    public void UnsignedComparisons_UseUnsignedPredicatesInIR()
    {
        var (exitCode, stdout, stderr) = RunCompiler(Fixture("UnsignedComparison", "RuntimeUnsigned"), "-O0", "--emit-ir");
        AssertCompilationSucceeded(exitCode, stdout, stderr, "RuntimeUnsigned.cvl (-O0 --emit-ir)");

        Assert.Contains("icmp ugt i32", stdout);
        Assert.Contains("icmp ult i32", stdout);
        Assert.Contains("icmp ugt i64", stdout);
        Assert.Contains("icmp ugt i8", stdout);
        Assert.Contains("icmp ugt i16", stdout);
        Assert.Contains("icmp slt i32", stdout);
        Assert.Contains("icmp slt i64", stdout);
        Assert.Contains("icmp slt i16", stdout);
        Assert.Contains("icmp slt i8", stdout);
    }

    [Fact]
    public void RelationalPredicates_AreDrivenByCanonicalCommonType()
    {
        var (_, context) = AnalyzeProject(Fixture("UnsignedComparison", "Bindings"));
        AssertNoSemanticErrors(context);

        var sameUnsigned = Assert.Single(context.ResolvedBinaryExpressions.Values,
            binding => binding.Operator == ">"
                && binding.LeftType?.Equals(TypeSymbol.UInt) == true
                && binding.RightType?.Equals(TypeSymbol.UInt) == true);
        Assert.Equal(BuiltinBinaryOperatorKind.IntegerComparison, sameUnsigned.BuiltinOperatorKind);
        Assert.Equal(TypeSymbol.UInt, sameUnsigned.OperationType);
        Assert.True(sameUnsigned.IsUnsignedIntegerComparison);

        var mixedSignedWidens = Assert.Single(context.ResolvedBinaryExpressions.Values,
            binding => binding.Operator == "<"
                && binding.LeftType?.Equals(TypeSymbol.Int) == true
                && binding.RightType?.Equals(TypeSymbol.Long) == true);
        Assert.Equal(BuiltinBinaryOperatorKind.IntegerComparison, mixedSignedWidens.BuiltinOperatorKind);
        Assert.Equal(TypeSymbol.Long, mixedSignedWidens.OperationType);
        Assert.False(mixedSignedWidens.IsUnsignedIntegerComparison);

        var mixedToUnsigned = Assert.Single(context.ResolvedBinaryExpressions.Values,
            binding => binding.Operator == ">"
                && binding.LeftType?.Equals(TypeSymbol.Byte) == true
                && binding.RightType?.Equals(TypeSymbol.UShort) == true);
        Assert.Equal(BuiltinBinaryOperatorKind.IntegerComparison, mixedToUnsigned.BuiltinOperatorKind);
        Assert.Equal(TypeSymbol.UShort, mixedToUnsigned.OperationType);
        Assert.True(mixedToUnsigned.IsUnsignedIntegerComparison);

        var charOrder = Assert.Single(context.ResolvedBinaryExpressions.Values,
            binding => binding.Operator == "<"
                && binding.LeftType?.Equals(TypeSymbol.Char) == true
                && binding.RightType?.Equals(TypeSymbol.Char) == true);
        Assert.Equal(BuiltinBinaryOperatorKind.IntegerComparison, charOrder.BuiltinOperatorKind);
        Assert.Equal(TypeSymbol.Char, charOrder.OperationType);
        Assert.False(charOrder.IsUnsignedIntegerComparison);

        var userDefined = Assert.Single(context.ResolvedBinaryExpressions.Values,
            binding => binding.Operator == "<" && binding.IsUserDefined);
        Assert.True(userDefined.IsUserDefined);
        Assert.NotNull(userDefined.OperatorOverload);
        Assert.False(userDefined.IsUnsignedIntegerComparison);
    }
}