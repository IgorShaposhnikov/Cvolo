using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class AttributeDiagnosticRangeTests
{
    [Theory]
    [InlineData("[SuppressWarning(\"123\")]", "\"123\"")]
    [InlineData("[SuppressWarning(\"CVL9999\")]", "\"CVL9999\"")]
    public void UnknownWarningId_UnderlinesOnlyArgument(string attribute, string invalidLiteral)
    {
        var source = attribute + "\npublic struct Example { int Value; }\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var diagnostic = Assert.Single(document.GetDiagnostics().Where(d => d.Message.Contains("Unknown warning id", StringComparison.Ordinal)));
        Assert.Equal(source.IndexOf(invalidLiteral, StringComparison.Ordinal), diagnostic.Location.Span.Start);
        Assert.Equal(invalidLiteral.Length, diagnostic.Location.Span.Length);
    }
}
