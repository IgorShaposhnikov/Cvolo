using Cvolo.Compiler.Tooling;
using Cvolo.Tests.Shared;
using Xunit;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// G20/G31: malformed nested generic syntax must stay actionable across
/// immutable document edits, without stale diagnostic or semantic state.
/// The source of each revision is stored in a separate .cvl fixture.
/// </summary>
public sealed class MalformedNestedGenericRecoveryTests
{
    private const string Folder = "MalformedNestedGenerics/Editor";

    private static string Source(string file) => TestCases.ReadText($"{Folder}/{file}.cvl");

    /// <summary>
    /// G20/G31: invalid -> valid -> invalid reparses must not keep old generic
    /// bindings, duplicate diagnostics, or return a success-shaped invalid AST.
    /// </summary>
    [Fact]
    public void ReplacingMalformedGeneric_UpdatesDiagnosticsAcrossSnapshots()
    {
        using var fixture = TempProject.Create(("Main.cvl", Source("Before")));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var id = project.GetDocumentId("Main.cvl");
        var initial = project.InitialSnapshot;

        var initialDiagnostics = initial.GetDocument(id).GetDiagnostics();
        Assert.Contains(initialDiagnostics, d => d.Id == "CVL4174"
            && d.Message.Contains("type argument", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(initialDiagnostics, d => d.Id is "CVL4019" or "CVL4093");

        var corrected = initial.WithDocument(id, SourceText.From(Source("After")));
        var correctedDiagnostics = corrected.GetDocument(id).GetDiagnostics();
        Assert.DoesNotContain(correctedDiagnostics, d => d.Id is "CVL4174" or "CVL4019" or "CVL4093");

        var invalidAgain = corrected.WithDocument(id, SourceText.From(Source("Again")));
        var finalDiagnostics = invalidAgain.GetDocument(id).GetDiagnostics();
        Assert.Contains(finalDiagnostics, d => d.Id == "CVL4174"
            && d.Message.Contains("type argument", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(finalDiagnostics, d => d.Id is "CVL4019" or "CVL4093");

        // Old snapshots are immutable and must keep their own diagnostics.
        Assert.Contains(initial.GetDocument(id).GetDiagnostics(), d => d.Id == "CVL4174");
    }

    /// <summary>
    /// G25-G26: consecutive and spaced opening angles remain equivalent
    /// syntax failures in the editor, even though their lexer tokens differ.
    /// </summary>
    [Theory]
    [InlineData("Before")]
    [InlineData("Again")]
    public void EditorReportsExpectedTypeForBothAngleSpellings(string fixtureName)
    {
        var source = Source(fixtureName);
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var findings = document.GetDiagnostics();
        var root = Assert.Single(findings, d => d.Id == "CVL4174"
            && d.Message.Contains("type argument", StringComparison.OrdinalIgnoreCase));
        var opening = source.LastIndexOf(fixtureName == "Again" ? "Pair<<" : "Pair< <", StringComparison.Ordinal);
        Assert.Equal(opening + (fixtureName == "Again" ? 5 : 6), root.Location.Span.Start);
    }
}
