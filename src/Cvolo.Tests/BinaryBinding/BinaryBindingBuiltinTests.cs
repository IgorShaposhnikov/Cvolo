using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Tests.BinaryBinding;

public sealed class BinaryBindingBuiltinTests : BinaryBindingTestBase
{
    [Fact]
    public void DirectFloatArgument_BindsAsFloat_AndSelectsFloatOverload()
    {
        var (_, context) = AnalyzeProject(Fixture("Runtime", "DirectFloatArgument"));
        AssertNoSemanticErrors(context);

        var multiplications = context.ResolvedBinaryExpressions.Values
            .Where(binding => binding.Operator == "*"
                && binding.LeftType?.Equals(TypeSymbol.Float) == true
                && binding.RightType?.Equals(TypeSymbol.Float) == true)
            .ToArray();

        Assert.Equal(2, multiplications.Length);
        Assert.All(multiplications, binding =>
        {
            Assert.True(binding.IsSuccess);
            Assert.Equal(BuiltinBinaryOperatorKind.FloatingArithmetic, binding.BuiltinOperatorKind);
            Assert.Equal(TypeSymbol.Float, binding.OperationType);
            Assert.Equal(TypeSymbol.Float, binding.ResultType);
            Assert.Null(binding.OperatorOverload);
            Assert.Equal(TypeSymbol.Float, binding.LeftConversion!.TargetType);
            Assert.Equal(TypeSymbol.Float, binding.RightConversion!.TargetType);
        });

        var select = Assert.Single(context.ResolvedCalls, entry => entry.Key.FunctionName == "Select").Value;
        Assert.Equal(TypeSymbol.Float, Assert.Single(select.Parameters).Type);
    }

    [Fact]
    public void MixedNumericBinding_StoresTheExactOperandPromotion()
    {
        var (_, context) = AnalyzeProject(Fixture("Runtime", "MixedNumeric"));
        AssertNoSemanticErrors(context);

        var binding = Assert.Single(context.ResolvedBinaryExpressions.Values,
            value => value.Operator == "*" && value.LeftType?.Equals(TypeSymbol.Int) == true
                && value.RightType?.Equals(TypeSymbol.Float) == true);

        Assert.Equal(BuiltinBinaryOperatorKind.FloatingArithmetic, binding.BuiltinOperatorKind);
        Assert.Equal(TypeSymbol.Float, binding.ResultType);
        Assert.Equal(BinaryNumericConversionKind.IntegerToFloating, binding.LeftConversion!.NumericKind);
        Assert.Equal(TypeSymbol.Float, binding.LeftConversion.TargetType);
        Assert.True(binding.RightConversion!.IsIdentity);
    }

    [Fact]
    public void SafeReferenceOperands_RecordDereferenceConversionsWithoutChangingTheResultType()
    {
        var (_, context) = AnalyzeProject(Fixture("Builtins", "ReferenceOperands"));
        AssertNoSemanticErrors(context);

        var binding = Assert.Single(context.ResolvedBinaryExpressions.Values,
            value => value.Operator == "+"
                && value.LeftType is PointerTypeSymbol
                && value.RightType is PointerTypeSymbol);

        Assert.Equal(TypeSymbol.Int, binding.ResultType);
        Assert.True(binding.LeftConversion!.Dereference);
        Assert.True(binding.RightConversion!.Dereference);
        Assert.Equal(TypeSymbol.Int, binding.LeftConversion.ValueType);
        Assert.Equal(TypeSymbol.Int, binding.RightConversion.ValueType);
    }

    [Fact]
    public void FlagsShiftsAndShortCircuitOperators_AllUseCanonicalBuiltinKinds()
    {
        var (_, flagsContext) = AnalyzeProject(Fixture("Builtins", "FlagsInline"));
        AssertNoSemanticErrors(flagsContext);
        Assert.Contains(flagsContext.ResolvedBinaryExpressions.Values,
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.FlagsBitwise
                && binding.ResultType?.Name == "MyFlags");

        var (_, shiftContext) = AnalyzeProject(Fixture("Builtins", "ShiftInline"));
        AssertNoSemanticErrors(shiftContext);
        Assert.Equal(2, shiftContext.ResolvedBinaryExpressions.Values.Count(
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.ShiftLeft
                && binding.ResultType?.Equals(TypeSymbol.Int) == true));

        var (_, logicalContext) = AnalyzeProject(Fixture("Builtins", "ShortCircuit"));
        AssertNoSemanticErrors(logicalContext);
        Assert.Contains(logicalContext.ResolvedBinaryExpressions.Values,
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.BooleanLogicalAnd);
        Assert.Contains(logicalContext.ResolvedBinaryExpressions.Values,
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.BooleanLogicalOr);
    }

    [Fact]
    public void RawPointerArithmetic_IsRepresentedByCanonicalBuiltinBindings()
    {
        var (_, context) = AnalyzeProject(Fixture("Builtins", "RawPointerArithmetic"));
        AssertNoSemanticErrors(context);

        var additions = context.ResolvedBinaryExpressions.Values
            .Where(binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.RawPointerAdd)
            .ToArray();
        var subtraction = Assert.Single(context.ResolvedBinaryExpressions.Values,
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.RawPointerSubtract);

        Assert.True(additions.Length >= 4);
        Assert.All(additions, AssertRawIntPointerResult);
        AssertRawIntPointerResult(subtraction);
    }

    [Fact]
    public void PointerLikeAndStringComparisons_RemainCanonicalBuiltinOperations()
    {
        var (_, stringContext) = AnalyzeProject(Fixture("Builtins", "StringComparison"));
        AssertNoSemanticErrors(stringContext);
        Assert.Contains(stringContext.ResolvedBinaryExpressions.Values,
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.StringComparison
                && binding.ResultType?.Equals(TypeSymbol.Bool) == true);

        var (_, delegateContext) = AnalyzeProject(Fixture("Builtins", "NativeDelegateNull"));
        AssertNoSemanticErrors(delegateContext);
        Assert.Equal(2, delegateContext.ResolvedBinaryExpressions.Values.Count(
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.NativeDelegateEquality));

        var (_, pointerContext) = AnalyzeProject(Fixture("Builtins", "RawPointerArithmetic"));
        AssertNoSemanticErrors(pointerContext);
        Assert.Contains(pointerContext.ResolvedBinaryExpressions.Values,
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.RawPointerComparison
                && binding.ResultType?.Equals(TypeSymbol.Bool) == true);
    }
}
