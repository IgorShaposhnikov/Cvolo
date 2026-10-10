using Xunit;
using Cvolo.Compiler.Tooling;
using Cvolo.Tests.Shared;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Only IDE/workspace snapshot behavior lives here. Sources are .cvl fixtures,
/// copied by the project's existing TestCases item rule.
/// </summary>
public sealed class InitializerDiagnosticsRegressionTests
{
    /// <summary>
    /// T22: replacing a document clears derived errors only in the new immutable workspace snapshot.
    /// </summary>
    [Fact]
    public void FixingAnUnknownFieldType_RebindsNewSnapshotWithoutMutatingOldOne()
    {
        using var fixture = TempProject.FromFixtureDirectory("InitializerDiagnostics/Editor/Before");
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var id = project.GetDocumentId("Main.cvl");
        var oldSnapshot = project.InitialSnapshot;
        Assert.Single(oldSnapshot.GetDocument(id).GetDiagnostics(), d => d.Id == "CVL4135");

        var correctedSource = TestCases.ReadText("InitializerDiagnostics/Editor/After/Fixed.cvl");
        var corrected = oldSnapshot.WithDocument(id, SourceText.From(correctedSource));
        Assert.DoesNotContain(corrected.GetDocument(id).GetDiagnostics(),
            d => d.Id is "CVL4135" or "CVL4093" or "CVL4019");
        Assert.Single(oldSnapshot.GetDocument(id).GetDiagnostics(), d => d.Id == "CVL4135");
    }

    /// <summary>
    /// P10: two GetDiagnostics calls must observe one stable field-conversion diagnostic and span.
    /// </summary>
    [Fact]
    public void RepeatedQueries_ReturnSameFieldConversionDiagnostic()
    {
        using var fixture = TempProject.FromFixtureDirectory("InitializerDiagnostics/RepeatedQueries");
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var first = document.GetDiagnostics().Where(d => d.Id == "CVL4019").ToArray();
        var second = document.GetDiagnostics().Where(d => d.Id == "CVL4019").ToArray();
        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].Location.Span, second[0].Location.Span);
    }

    /// <summary>
    /// T05: IDE diagnostics belong to the declaring document, not to every separate use.
    /// </summary>
    [Fact]
    public void CrossFileUnknownType_IsReportedAtDeclarationNotEveryInitializerUse()
    {
        using var fixture = TempProject.FromFixtureDirectory("InitializerDiagnostics/MultiFile");
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var snapshot = project.InitialSnapshot;
        var declaration = snapshot.GetDocument(project.GetDocumentId("Types.cvl")).GetDiagnostics();
        var usage = snapshot.GetDocument(project.GetDocumentId("Main.cvl")).GetDiagnostics();
        Assert.Single(declaration, d => d.Id == "CVL4135");
        Assert.DoesNotContain(usage, d => d.Id is "CVL4135" or "CVL4093" or "CVL4019");
    }
}
