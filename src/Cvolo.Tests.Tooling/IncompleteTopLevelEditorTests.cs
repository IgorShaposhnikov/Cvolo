using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

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
    [InlineData("int x = 1;\nint x\nwhile (false) {}", true)]
    [InlineData("int x = 1;\nint y\nwhile (false) {}", false)]
    [InlineData("{ int x = 1; }\nint x\nwhile (false) {}", false)]
    public void IncompleteLocal_NameAlreadyDeclaredInSameBrace_ReportsDuplicate(
        string body, bool duplicate)
    {
        var source = "void Go() {\n" + body + "\n}\n";
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var diagnostics = document.GetDiagnostics();
            Assert.Equal(duplicate, diagnostics.Any(d =>
                d.Id == "CVL4140" && d.Message.Contains("x", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData("extension |", new string[] { "Third", "Second", "First" })]
    [InlineData("extension Se|", new string[] { "Second" })]
    [InlineData("extension Win|", new string[] { })]
    public void ExtensionTarget_OffersNearestDeclaredTypesOnly(
        string input, string[] expected)
    {
        var position = input.IndexOf('|');
        var source = "struct First {}\nstruct Second {}\nstruct Third {}\n" 
            + input.Replace("|", "", StringComparison.Ordinal);
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var result = document.GetCompletions(source.Length);
            var names = result.Candidates.Select(c => c.Label).ToArray();
            Assert.Equal(expected, names);
            Assert.All(result.Candidates, c => Assert.Equal(CompletionKind.Type, c.Kind));
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
