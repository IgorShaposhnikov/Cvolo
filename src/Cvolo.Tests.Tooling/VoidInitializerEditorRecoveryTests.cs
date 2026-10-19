using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class VoidInitializerEditorRecoveryTests
{
    private const string Preamble = """
        struct Window { int glfwWindow; }
        void SwapBuffers(int handle) {}
        bool Init() { return true; }
        """;

    private static (DocumentSnapshot Document, string Source) Open(string body)
    {
        var source = Preamble + "\nextension Window {\n"
            + "    public void Swap(ref this) {\n"
            + body + "\n"
            + "    }\n}\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        return (project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl")), source);
    }

    [Fact]
    public void VoidCallOnNextLine_ReportsTypeMismatchRatherThanSplittingInitializer()
    {
        var (document, _) = Open("        bool x =\n        SwapBuffers(glfwWindow);");
        var diagnostics = document.GetDiagnostics();

        Assert.Contains(diagnostics, d =>
            d.Message.Contains("Cannot initialize variable of type 'bool' with value of type 'void'",
                StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4174");

        var tokens = document.GetSemanticTokens();
        Assert.Contains(tokens, token =>
            document.Text.GetText(token.Span) == "x" && token.Kind == ToolingSymbolKind.Local);
        Assert.DoesNotContain(tokens, token =>
            document.Text.GetText(token.Span) == "SwapBuffers" && token.Kind == ToolingSymbolKind.Local);
    }

    [Fact]
    public void BooleanCallOnNextLine_RemainsLegalInitializer()
    {
        var (document, _) = Open("        bool x =\n        Init();");
        Assert.Empty(document.GetDiagnostics());
    }

    [Fact]
    public void VoidCallOnSameLine_PreservesOrdinaryTypeMismatch()
    {
        var (document, _) = Open("        bool x = SwapBuffers(glfwWindow);");
        Assert.Contains(document.GetDiagnostics(), d =>
            d.Message.Contains("Cannot initialize variable of type 'bool' with value of type 'void'",
                StringComparison.Ordinal));
        Assert.DoesNotContain(document.GetDiagnostics(), d => d.Id == "CVL4174");
    }

    [Fact]
    public void CompleteSplitAssignmentAndSubsequentCallRemainIndependent()
    {
        var (document, _) = Open("        bool x =\n        Init();\n        SwapBuffers(glfwWindow);");
        Assert.Empty(document.GetDiagnostics());
    }
}
