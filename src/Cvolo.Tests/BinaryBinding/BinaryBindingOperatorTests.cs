using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Tests.BinaryBinding;

public sealed class BinaryBindingOperatorTests : BinaryBindingTestBase
{
    [Fact]
    public void UserDefinedOperator_InlineAndLocalFormsShareTheSameSelectedOverloadSemantics()
    {
        var (_, context) = AnalyzeProject(Fixture("Operators", "OperatorInline"));
        AssertNoSemanticErrors(context);

        var bindings = context.ResolvedBinaryExpressions.Values
            .Where(binding => binding.Operator == "+" && binding.OperatorOverload is not null)
            .ToArray();

        Assert.Equal(2, bindings.Length);
        Assert.Equal(bindings[0].OperatorOverload!.Name, bindings[1].OperatorOverload!.Name);
        Assert.Equal(bindings[0].ResultType, bindings[1].ResultType);
        Assert.Equal(bindings[0].LeftConversion, bindings[1].LeftConversion);
        Assert.Equal(bindings[0].RightConversion, bindings[1].RightConversion);
        Assert.All(bindings, binding => Assert.True(binding.IsUserDefined));

        var convertedBindings = context.ResolvedBinaryExpressions.Values
            .Where(binding => binding.Operator == "*" && binding.OperatorOverload is not null)
            .ToArray();
        Assert.Equal(2, convertedBindings.Length);
        Assert.All(convertedBindings, binding =>
        {
            Assert.Equal(BinaryNumericConversionKind.IntegerToFloating, binding.RightConversion!.NumericKind);
            Assert.Equal(TypeSymbol.Float, binding.RightConversion.TargetType);
            Assert.Equal("Vec2", binding.ResultType!.Name);
        });
    }

    [Fact]
    public void OperatorBodies_AreTraversedAndTheirBuiltinChildrenAreCanonicallyBound()
    {
        var (_, context) = AnalyzeProject(Fixture("Operators", "OperatorInline"));
        AssertNoSemanticErrors(context);

        // Vec2.X/Y are int fields in this fixture. The operator body's left.X + right.X / left.Y + right.Y
        // must therefore be canonical integer arithmetic; the previous floating assertion was simply wrong.
        Assert.True(context.ResolvedBinaryExpressions.Values.Count(binding =>
            binding.OperatorOverload is null
            && binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.IntegerArithmetic
            && binding.Operator == "+"
            && binding.LeftType?.Equals(TypeSymbol.Int) == true
            && binding.RightType?.Equals(TypeSymbol.Int) == true) >= 2);
    }
}
