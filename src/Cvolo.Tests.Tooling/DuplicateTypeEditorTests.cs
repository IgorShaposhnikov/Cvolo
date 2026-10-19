using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class DuplicateTypeEditorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalWindow_DoesNotReplaceNamespacedWindowOrItsFields(bool globalFirst)
    {
        const string main = "namespace App;\nstruct Window { void* glfwWindow; }\n"
            + "public extension Window {\n"
            + "    public bool IsInit() { return glfwWindow == null; }\n"
            + "}\n";
        const string second = "struct Window {}\n";
        using var fixture = globalFirst
            ? TempProject.Create(("Test.cvl", second), ("Main.cvl", main))
            : TempProject.Create(("Main.cvl", main), ("Test.cvl", second));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var mainDocument = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var testDocument = project.InitialSnapshot.GetDocument(project.GetDocumentId("Test.cvl"));
        Assert.DoesNotContain(mainDocument.GetDiagnostics(), d =>
            d.Message.Contains("Undefined variable 'glfwWindow'", StringComparison.Ordinal)
            || d.Message.Contains("Duplicate type definition", StringComparison.Ordinal));
        Assert.DoesNotContain(testDocument.GetDiagnostics(), d =>
            d.Id == "CVL4054");
    }

    [Theory]
    [InlineData("struct Window {}\n", true)]
    [InlineData("struct Window\n{\n}\n", true)]
    [InlineData("struct Window\n", false)]
    public void CrossFileDuplicateType_IsReportedOnlyForCompletedDeclaration(string second, bool complete)
    {
        using var fixture = TempProject.Create(
            ("Main.cvl", "struct Window { int Width; int Height; }\n"),
            ("Test.cvl", second));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var test = project.InitialSnapshot.GetDocument(project.GetDocumentId("Test.cvl"));
        var main = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var errors = test.GetDiagnostics();
        if (complete)
        {
            Assert.Contains(errors, d => d.Id == "CVL4054"
                && d.Message.Contains("Window", StringComparison.Ordinal));
        }
        if (complete)
        {
            // A duplicate empty editor struct must not become the canonical
            // Window that provides fields for the existing Main.cvl code.
            Assert.DoesNotContain(main.GetDiagnostics(), d =>
                d.Message.Contains("Unknown field", StringComparison.OrdinalIgnoreCase));
        }
        // The original declaration must remain available in navigation even
        // while a competing file is edited.
        Assert.Contains(main.GetDocumentSymbols(), s => s.Name == "Window");
    }
}
