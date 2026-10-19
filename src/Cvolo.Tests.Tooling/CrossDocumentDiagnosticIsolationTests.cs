using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// An invalid editor buffer must not silence binder diagnostics in unrelated
/// source files of the same workspace/project generation.
/// </summary>
public sealed class CrossDocumentDiagnosticIsolationTests
{
    private const string BrokenSource = """
        void Broken() {
            int value
            if (true) {}
        }
        """;

    private const string SemanticallyInvalidSource = """
        void SwapBuffers() {}
        void BrokenLocal() {
            void x;
            void* pointer;
        }
        void BrokenInitializer() {
            bool x =
                SwapBuffers();
        }
        """;

    [Fact]
    public void SyntaxErrorsInNeighboringFile_DoNotSuppressVoidAndInitializerTypeErrors()
    {
        using var fixture = TempProject.Create(
            ("Broken.cvl", BrokenSource),
            ("ValidSyntax.cvl", SemanticallyInvalidSource));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

        var damaged = project.InitialSnapshot.GetDocument(project.GetDocumentId("Broken.cvl"));
        var healthy = project.InitialSnapshot.GetDocument(project.GetDocumentId("ValidSyntax.cvl"));

        Assert.Contains(damaged.GetDiagnostics(), d =>
            d.Id is "CVL4173" or "CVL4174");
        var diagnostics = healthy.GetDiagnostics();
        Assert.Contains(diagnostics, d =>
            d.Id == "CVL4220"
            && d.Location.Span.Start == SemanticallyInvalidSource.IndexOf("void x;", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d =>
            d.Id == "CVL4020"
            && d.Message.Contains("value of type 'void'", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, d => d.Id is "CVL4173" or "CVL4174");
        Assert.DoesNotContain(diagnostics, d => d.Location.Span.Start ==
            SemanticallyInvalidSource.IndexOf("void* pointer;", StringComparison.Ordinal));
    }

    [Fact]
    public void SeparateMalformedFile_PreservesValidDocumentSymbolAndTokens()
    {
        using var fixture = TempProject.Create(
            ("Broken.cvl", BrokenSource),
            ("ValidSyntax.cvl", "void Alive() { int x = 1; }\n"));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("ValidSyntax.cvl"));

        Assert.Empty(document.GetDiagnostics());
        Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Alive");
        Assert.Contains(document.GetSemanticTokens(), token =>
            document.Text.GetText(token.Span) == "x"
            && token.Kind == ToolingSymbolKind.Local);
    }
}
