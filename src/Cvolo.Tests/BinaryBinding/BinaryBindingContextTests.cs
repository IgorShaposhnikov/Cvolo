using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Tests.BinaryBinding;

public sealed class BinaryBindingContextTests : BinaryBindingTestBase
{
    [Fact]
    public void NestedAndLogicalExpressions_AreBoundBottomUpIntoOneSemanticGraph()
    {
        var (_, context) = AnalyzeProject(Fixture("Runtime", "ContextEquivalence"));
        AssertNoSemanticErrors(context);

        var bindings = context.ResolvedBinaryExpressions.Values.ToArray();
        Assert.Contains(bindings, binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.BooleanLogicalAnd
            && binding.ResultType?.Equals(TypeSymbol.Bool) == true);
        Assert.Contains(bindings, binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.FloatingComparison
            && binding.ResultType?.Equals(TypeSymbol.Bool) == true);
        Assert.Contains(bindings, binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.FloatingArithmetic
            && binding.ResultType?.Equals(TypeSymbol.Float) == true);
        Assert.Contains(bindings, binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.FloatingArithmetic
            && binding.ResultType?.Equals(TypeSymbol.Double) == true);
        Assert.Contains(bindings, binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.IntegerArithmetic
            && binding.ResultType?.Equals(TypeSymbol.Int) == true);
        Assert.All(bindings, binding => Assert.Same(binding.Syntax, context.ResolvedBinaryExpressions[binding.Syntax].Syntax));
    }

    [Fact]
    public void GenericSpecializations_DoNotShareBinaryBindingsAcrossSubstitutions()
    {
        var (_, context) = AnalyzeProject(Fixture("Generics", "GenericSpecialization"));
        AssertNoSemanticErrors(context);

        var intBinding = Assert.Single(context.ResolvedBinaryExpressions.Values,
            value => value.Operator == "+" && value.LeftType?.Equals(TypeSymbol.Int) == true
                && value.RightType?.Equals(TypeSymbol.Int) == true);
        var floatBinding = Assert.Single(context.ResolvedBinaryExpressions.Values,
            value => value.Operator == "+" && value.LeftType?.Equals(TypeSymbol.Float) == true
                && value.RightType?.Equals(TypeSymbol.Float) == true);

        Assert.NotSame(intBinding.Syntax, floatBinding.Syntax);
        Assert.Equal(TypeSymbol.Int, intBinding.ResultType);
        Assert.Equal(BuiltinBinaryOperatorKind.IntegerArithmetic, intBinding.BuiltinOperatorKind);
        Assert.Equal(TypeSymbol.Float, floatBinding.ResultType);
        Assert.Equal(BuiltinBinaryOperatorKind.FloatingArithmetic, floatBinding.BuiltinOperatorKind);
    }

    [Fact]
    public void CrossFileOperator_IsBoundOnlyAfterTheDeclarationUniverseIsStable()
    {
        var (_, context) = AnalyzeProject(FixtureDirectory("DeclarationTiming", "CrossFileTiming"));
        AssertNoSemanticErrors(context);

        var binding = Assert.Single(context.ResolvedBinaryExpressions.Values,
            value => value.Operator == "+" && value.LeftType?.Name == "Cell" && value.RightType?.Name == "Cell");
        Assert.True(binding.IsSuccess);
        Assert.NotNull(binding.OperatorOverload);
        Assert.Equal("Cell", binding.ResultType!.Name);
        Assert.Equal(BinaryBindingErrorKind.None, binding.ErrorKind);
    }
}
