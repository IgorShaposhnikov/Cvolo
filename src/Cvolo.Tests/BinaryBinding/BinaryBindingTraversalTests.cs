using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Tests.BinaryBinding;

public sealed class BinaryBindingTraversalTests : BinaryBindingTestBase
{
    [Fact]
    public void BinaryNestedInsideIndex_IsBoundBeforeCodegen()
    {
        var (_, context) = AnalyzeProject(Fixture("Traversal", "IndexOperand"));
        AssertNoSemanticErrors(context);

        var subtraction = Assert.Single(context.ResolvedBinaryExpressions.Values,
            binding => binding.Operator == "-"
                && binding.LeftType?.Equals(TypeSymbol.Int) == true
                && binding.RightType?.Equals(TypeSymbol.Int) == true);
        Assert.Equal(BuiltinBinaryOperatorKind.IntegerArithmetic, subtraction.BuiltinOperatorKind);
        Assert.Equal(TypeSymbol.Int, subtraction.ResultType);
    }

    [Fact]
    public void GlobalConstantBinaryExpressions_AreAlsoCanonicallyBound()
    {
        var (_, context) = AnalyzeProject(Fixture("Runtime", "GlobalConstant"));
        AssertNoSemanticErrors(context);

        Assert.Contains(context.ResolvedBinaryExpressions.Values,
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.FloatingArithmetic
                && binding.ResultType?.Equals(TypeSymbol.Float) == true);
        Assert.Contains(context.ResolvedBinaryExpressions.Values,
            binding => binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.IntegerArithmetic
                && binding.ResultType?.Equals(TypeSymbol.Int) == true);
    }
}
