using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.CodeLens;

namespace Cvolo.Tests.Tooling;

public sealed class IncompleteTopLevelEditorTests
{
    private static (DocumentSnapshot Document, TempProject Fixture) Open(string source)
    {
        var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        return (project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
    }

    [Theory]
    [InlineData("str|")]
    [InlineData("stru|")]
    [InlineData("struc|")]
    [InlineData("struct|")]
    public void FreshFile_OffersStructKeyword(string input)
    {
        var position = input.IndexOf('|');
        var source = input.Replace("|", "", StringComparison.Ordinal);
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var completions = document.GetCompletions(position);
            Assert.Contains(completions.Candidates, c =>
                c.Label == "struct" && c.Kind == CompletionKind.Keyword);
        }
    }

    [Theory]
    [InlineData("struct GlfwWindow {\n    int\n}\n")]
    [InlineData("struct GlfwWindow {\n    int Width;\n    int\n}\n")]
    public void IncompleteField_RetainsStructReferenceCodeLens(string source)
    {
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var lenses = document.GetCodeLenses();
            Assert.Contains(lenses, c =>
                c.Kind == ToolingCodeLensKind.References
                && document.Text.GetText(c.Range) == "GlfwWindow");
            Assert.Contains(document.GetDiagnostics(), d =>
                d.Message.Contains("field name", StringComparison.OrdinalIgnoreCase));
        }
    }
}
