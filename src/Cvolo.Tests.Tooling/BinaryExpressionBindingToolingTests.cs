using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class BinaryExpressionBindingToolingTests
{
    [Fact]
    public void InlineFloatBinaryArgument_DrivesTheSameResolvedSignatureAsTheCompiler()
    {
        const string source =
            "int Select(int value) { return 1; }\n" +
            "int Select(float value) { return 2; }\n" +
            "int main() { float time = 2.0f; return Select(time * 0.5f); }\n";

        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

        Assert.DoesNotContain(document.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var position = source.IndexOf("time * 0.5f", StringComparison.Ordinal) + 1;
        var help = document.GetSignatureHelp(position);

        Assert.NotNull(help);
        Assert.Equal("int Select(float value)", help!.Signatures[help.ActiveSignature].Label);
    }

    [Fact]
    public void InferredLocalTypeHint_UsesTheCanonicalBinaryResultType()
    {
        const string source =
            "int main() { float time = 2.0f; val product = time * 0.5f; return product == 1.0f ? 0 : 1; }\n";

        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

        var hints = document.GetInlayHints(0, source.Length);
        var productNameEnd = source.IndexOf("product", StringComparison.Ordinal) + "product".Length;
        var hint = Assert.Single(hints, candidate =>
            candidate.Kind == ToolingInlayHintKind.Type && candidate.Position == productNameEnd);

        Assert.Equal(": float", hint.Label);
    }

    [Fact]
    public void InvalidBinaryDiagnostic_IsStableAcrossRepeatedToolingQueries()
    {
        const string source =
            "struct A { int X; }\n" +
            "int main() { val A a(X: 1); val A b(X: 2); A x = a * b; return x.X; }\n";

        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

        var first = document.GetDiagnostics();
        var second = document.GetDiagnostics();

        Assert.Single(first, diagnostic => diagnostic.Id == "CVL2205");
        Assert.Single(second, diagnostic => diagnostic.Id == "CVL2205");
        Assert.Equal(first.Select(diagnostic => (diagnostic.Id, diagnostic.Message)),
            second.Select(diagnostic => (diagnostic.Id, diagnostic.Message)));
    }
}
