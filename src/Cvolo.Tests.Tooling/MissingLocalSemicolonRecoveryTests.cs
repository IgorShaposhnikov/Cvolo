using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>Unambiguous statement boundaries after a typed local with no semicolon.</summary>
public sealed class MissingLocalSemicolonRecoveryTests
{
    [Theory]
    [InlineData("void Go() {\n    bool x\n    SwapBuffers(1);\n}")]
    [InlineData("void Go() {\n    if (true) {\n        bool x\n        SwapBuffers(1);\n    }\n}")]
    [InlineData("void Go() {\n    while (true) {\n        bool x\n        SwapBuffers(1);\n        break;\n    }\n}")]
    [InlineData("extension Window {\n    public void Swap(ref this) {\n        bool x\n        SwapBuffers(glfwWindow);\n    }\n}")]
    [InlineData("void Go() {\n    bool x\n    System.Console.WriteLine(1);\n}")]
    public void MissingSemicolonBeforeCall_AnchorsToDeclarationAndKeepsLocalAndCall(string body)
    {
        var source = "using System;\nstruct Window { int glfwWindow; }\nvoid SwapBuffers(int handle) {}\n"
            + body + "\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

        var endOfLocal = source.IndexOf("bool x", StringComparison.Ordinal) + "bool x".Length;
        var nextCall = source.IndexOf("SwapBuffers(", endOfLocal, StringComparison.Ordinal);
        if (nextCall < 0)
            nextCall = source.IndexOf("System.Console.WriteLine(", endOfLocal, StringComparison.Ordinal);

        var diagnostics = document.GetDiagnostics();
        var error = Assert.Single(diagnostics);
        Assert.Equal("CVL4174", error.Id);
        Assert.Equal(endOfLocal, error.Location.Span.Start);
        Assert.Equal(0, error.Location.Span.Length);
        Assert.DoesNotContain(diagnostics, d => d.Location.Span.Start >= nextCall);

        Assert.Contains(document.GetSemanticTokens(), token =>
            document.Text.GetText(token.Span) == "x"
            && token.Kind == ToolingSymbolKind.Local);
    }

    [Fact]
    public void MissingSemicolonWithoutIndent_DoesNotSwallowCall()
    {
        const string source = "void SwapBuffers(int x) {}\nvoid Go() {\n    bool x\nSwapBuffers(1);\n}\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var errors = document.GetDiagnostics();
        var finding = Assert.Single(errors);
        Assert.Equal("CVL4174", finding.Id);
        Assert.Equal(source.IndexOf("bool x", StringComparison.Ordinal) + "bool x".Length,
            finding.Location.Span.Start);
    }

    [Fact]
    public void LegalMultilineTypedDeclaration_RemainsUntouched()
    {
        const string source = "void Go() {\n    bool\n    x;\n}\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        Assert.Empty(document.GetDiagnostics());
    }
}
