using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class DuplicateTypeEditorTests
{
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
        // The original declaration must remain available in navigation even
        // while a competing file is edited.
        Assert.Contains(main.GetDocumentSymbols(), s => s.Name == "Window");
    }
}
