using Cvolo.Analysis;
using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.Diagnostics;
using Cvolo.Tests.Core;

namespace Cvolo.Tests.BinaryBinding;

public abstract class BinaryBindingTestBase : CompilerTestBase
{
    protected const string Root = "BinaryBinding";

    protected static string Fixture(string group, string name) => $"{Root}/{group}/{name}.cvl";
    protected static string FixtureDirectory(string group, string name) => $"{Root}/{group}/{name}";

    protected static void AssertNoSemanticErrors(BindingContext context)
    {
        var errors = context.Diagnostics.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }

    protected static void AssertRawIntPointerResult(ResolvedBinaryExpression binding)
    {
        var pointer = Assert.IsType<RawPointerTypeSymbol>(binding.ResultType);
        Assert.Equal(TypeSymbol.Int, pointer.ElementType);
        Assert.NotNull(binding.LeftConversion);
        Assert.NotNull(binding.RightConversion);
    }
}
