using Cvolo.Analysis.Semantics;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Tests.BinaryBinding;

public sealed class BinaryBindingErrorTests : BinaryBindingTestBase
{
    [Fact]
    public void InvalidBinaryCallArgument_DoesNotProduceAFabricatedOverloadDiagnostic()
    {
        var (_, context) = AnalyzeProject(Fixture("Errors", "InvalidOperatorArgumentFail"));

        Assert.Single(context.Diagnostics.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.OperatorNoMatch
                && diagnostic.Message.Contains("not valid/applicable", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Diagnostics.Diagnostics,
            diagnostic => diagnostic.Message.Contains("No overload of function 'TakeInt'", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidOperator_ProducesOneFinalErrorBindingAndOnePrimaryDiagnostic()
    {
        var (_, context) = AnalyzeProject(Fixture("Errors", "InvalidOperatorFail"));

        var binding = Assert.Single(context.ResolvedBinaryExpressions.Values,
            value => value.Operator == "*" && value.LeftType?.Name == "A" && value.RightType?.Name == "A");
        Assert.Equal(BinaryBindingState.Error, binding.State);
        Assert.Equal(BinaryBindingErrorKind.InvalidOperator, binding.ErrorKind);
        Assert.Null(binding.ResultType);

        var sameBinding = context.ResolvedBinaryExpressions[binding.Syntax];
        Assert.Same(binding, sameBinding);
        Assert.Single(context.Diagnostics.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.OperatorNoMatch
                && diagnostic.Message.Contains("not valid/applicable", StringComparison.Ordinal));
    }
}
