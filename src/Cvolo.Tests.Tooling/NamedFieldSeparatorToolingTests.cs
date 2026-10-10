using Cvolo.Compiler.Tooling;
using Cvolo.Tests.Shared;
using Xunit;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// S17-S18: source-preserving document snapshots retain their original
/// separator token, and edits must not leak stale semantic diagnostics.
/// </summary>
public sealed class NamedFieldSeparatorToolingTests
{
    private const string Folder = "NamedFieldSeparators/Editor";
    private static string Source(string file) => TestCases.ReadText($"{Folder}/{file}.cvl");

    /// <summary>
    /// S17: requesting diagnostics must not silently rewrite user-selected
    /// ':' into '=' in a source-preserving tooling document.
    /// </summary>
    [Theory]
    [InlineData("BeforeColon", "X: 1")]
    [InlineData("AfterEquals", "X = 1")]
    public void Diagnostics_DoNotRewriteSourceSeparators(string file, string chosenSpelling)
    {
        var source = Source(file);
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        Assert.DoesNotContain(document.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(source, document.Text.ToString());
        Assert.Contains(chosenSpelling, document.Text.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// S18: colon to equals to colon reparsing must keep valid field binding
    /// with no duplicated diagnostics and independent immutable snapshots.
    /// </summary>
    [Fact]
    public void EditingSeparator_RebindsAndPreservesPreviousSnapshots()
    {
        var original = Source("BeforeColon");
        var withEquals = Source("AfterEquals");
        var backToColon = Source("BackToColon");
        using var fixture = TempProject.Create(("Main.cvl", original));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var id = project.GetDocumentId("Main.cvl");
        var initial = project.InitialSnapshot;
        var edited = initial.WithDocument(id, SourceText.From(withEquals));
        var restored = edited.WithDocument(id, SourceText.From(backToColon));

        Assert.DoesNotContain(initial.GetDocument(id).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(edited.GetDocument(id).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(restored.GetDocument(id).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(original, initial.GetDocument(id).Text.ToString());
        Assert.Equal(withEquals, edited.GetDocument(id).Text.ToString());
        Assert.Equal(backToColon, restored.GetDocument(id).Text.ToString());
    }
}
