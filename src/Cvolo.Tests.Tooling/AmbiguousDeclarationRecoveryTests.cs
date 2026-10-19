using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class AmbiguousDeclarationRecoveryTests
{
    private const string Prelude = """
        public struct Window { public int Value; }
        void Begin(int mode) {}
        """;

    private static (DocumentSnapshot Document, string Source) Open(string statement)
    {
        var source = Prelude + "\nvoid Go() {\n" + statement + "\n}\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        return (project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl")), source);
    }

    [Theory]
    [InlineData("Beg")]
    [InlineData("MissingType")]
    public void UnresolvedTypeBeforeCall_ReportsOnlyHeadAndPreservesCall(string head)
    {
        var (document, source) = Open(head + "\nBegin(1);");
        var errors = document.GetDiagnostics();
        var problem = Assert.Single(errors);
        Assert.Equal("CVL4023", problem.Id);
        Assert.Equal(source.IndexOf(head + "\nBegin", StringComparison.Ordinal), problem.Location.Span.Start);
        Assert.DoesNotContain(errors, d => d.Location.Span.Start >= source.IndexOf("Begin(1);", StringComparison.Ordinal));

        var tokens = document.GetSemanticTokens();
        Assert.DoesNotContain(tokens, t =>
            document.Text.GetText(t.Span) == "Begin"
            && t.Kind is ToolingSymbolKind.Local or ToolingSymbolKind.Parameter);
    }

    [Theory]
    [InlineData("Begi")]
    [InlineData("MissingType")]
    public void InvalidHeadBeforeExistingLocalAssignment_DoesNotDuplicateLocal(string head)
    {
        var (document, source) = Open("int s;\n" + head + "\ns = 1;");
        var diagnostics = document.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "CVL4023"
            && d.Location.Span.Start == source.IndexOf(head + "\ns =", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4140"
            || d.Message.Contains("already declared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidHeadBeforeExtensionFieldAssignment_DoesNotRedeclareField()
    {
        var source = """
            struct Window { int glfwWindow; }
            extension Window {
                void Swap(ref this) {
                    Begi
                    glfwWindow = 42;
                }
            }
            """;
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

        var diagnostics = document.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "CVL4023");
        Assert.DoesNotContain(diagnostics, d =>
            d.Message.Contains("already declared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownTypeWithFreshName_RemainsDeclaration()
    {
        var (document, _) = Open("MissingType\nnewName = 1;");
        Assert.Contains(document.GetDiagnostics(), d => d.Id == "CVL4023");
        Assert.DoesNotContain(document.GetDiagnostics(), d => d.Id == "CVL4174");
    }

    [Fact]
    public void BrokenHead_DoesNotHideRealImmutableAssignment()
    {
        var (document, _) = Open("bool x;\nBegi\nx = true;");
        var diagnostics = document.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "CVL4023");
        Assert.Contains(diagnostics, d => d.Message == "Cannot assign to immutable variable 'x'");
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("already declared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrokenHead_DoesNotHideTwoImmutableAssignments()
    {
        var (document, _) = Open("bool x;\nBegi\nx = true;\nx = false;");
        var diagnostics = document.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "CVL4023");
        Assert.Equal(2, diagnostics.Count(d => d.Message == "Cannot assign to immutable variable 'x'"));
    }

    [Fact]
    public void MissingSemicolonAfterCall_IsReportedBeforeNextAssignment()
    {
        var (document, source) = Open("int x;\nBegin(1)\nx = 1;");
        var beginEnd = source.IndexOf("Begin(1)", StringComparison.Ordinal) + "Begin(1)".Length;
        Assert.Contains(document.GetDiagnostics(), d =>
            d.Message.Contains("';'", StringComparison.Ordinal)
            && d.Location.Span.Start == beginEnd);
    }

    [Fact]
    public void BrokenTree_StillProducesDiagnosticsAndEditorQueries()
    {
        var (document, _) = Open("if (true) { Begin(\n}");
        Assert.NotNull(document.GetDiagnostics());
        Assert.NotNull(document.GetSemanticTokens());
    }

    [Fact]
    public void ResolvedTypeBeforeCall_RemainsLegalSplitDeclaration()
    {
        var (document, _) = Open("Window\nitem;");
        Assert.Empty(document.GetDiagnostics());
        Assert.Contains(document.GetSemanticTokens(), t => document.Text.GetText(t.Span) == "item");
    }

    [Fact]
    public void ExplicitCtorStyleInitializer_StaysSingleDeclaration()
    {
        var (document, _) = Open("Window item(1);");
        Assert.DoesNotContain(document.GetDiagnostics(), d => d.Id == "CVL4023");
    }

    [Fact]
    public void DeferredCallAcrossNewline_IsAllowed()
    {
        var (document, _) = Open("defer\nBegin(1);");
        Assert.DoesNotContain(document.GetDiagnostics(), d => d.Id is "CVL4004" or "CVL4174" or "CVL4173");
    }
}
