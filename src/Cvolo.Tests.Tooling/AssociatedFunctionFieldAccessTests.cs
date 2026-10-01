using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

public sealed class AssociatedFunctionFieldAccessTests
{
    private const string AssociatedSource = """
        public struct TestG { public int Ptr; }
        public extension TestG
        {
            public void .TestGF<T>() { Ptr = 0; }
        }
        """;

    private const string InstanceSource = """
        public struct TestG { public int Ptr; }
        public extension TestG
        {
            public void TestGFNoStatic<T>() { Ptr = 0; }
        }
        """;

    private static (DocumentSnapshot Document, IReadOnlyList<Diagnostic> Diagnostics) Open(string source)
    {
        var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        return (document, document.GetDiagnostics());
    }

    [Fact]
    public void AssociatedMethod_TouchingAnInstanceFieldWithoutAReceiver_IsReportedAgainstTheField()
    {
        var (document, diagnostics) = Open(AssociatedSource);

        var finding = Assert.Single(diagnostics, d => d.Id == "CVL2208");
        Assert.Equal("Ptr", document.Text.GetText(finding.Location.Span));
        Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4114");
    }

    [Fact]
    public void InstanceMethod_TouchingAnInstanceField_IsNotReported()
    {
        var (document, diagnostics) = Open(InstanceSource);

        var unexpected = diagnostics
            .Where(d => d.Id is "CVL2208" or "CVL4114")
            .Select(d => $"{d.Id}@{document.Text.GetText(d.Location.Span)}: {d.Message}")
            .ToList();
        Assert.True(unexpected.Count == 0, string.Join(" | ", unexpected));
    }
}
