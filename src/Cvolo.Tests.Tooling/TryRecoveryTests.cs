using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Parser-to-tooling regressions for try/catch and adjacent incomplete statements.
/// A recovered source must preserve independent later code and stable diagnostics.
/// </summary>
public sealed class TryRecoveryTests
{
    private static bool IsSyntax(Diagnostic diagnostic) =>
        diagnostic.Id is "CVL4004" or "CVL4173" or "CVL4174" or "CVL4175";

    private static (DocumentSnapshot Document, TempProject Fixture) Open(string source)
    {
        var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        return (project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
    }

    public static IEnumerable<object[]> MissingBodyCases()
    {
        foreach (var next in new[]
        {
            "while (false) { }", "if (true) { }", "return;",
            "for (int i = 0; i < 1; i = i + 1) { }",
            "switch (1) { default: break; }"
        })
        foreach (var newLine in new[] { "\n", "\r\n" })
            yield return [next, newLine];
    }

    [Theory]
    [MemberData(nameof(MissingBodyCases))]
    public void BareTryBeforeIndependentStatement_ReportsAtTryNotFollowingCode(
        string following, string newLine)
    {
        var source = ("void Go() {\n"
            + "    try\n"
            + "    " + following + "\n"
            + "    int afterProbe = 7;\n"
            + "}\n").Replace("\n", newLine);
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var findings = document.GetDiagnostics();
            var syntax = findings.Where(IsSyntax).ToArray();
            var tryStart = source.IndexOf("    try", StringComparison.Ordinal) + 4;
            var followingStart = source.IndexOf(following, tryStart, StringComparison.Ordinal);
            var roots = syntax.Where(d => d.Location.Span.Start >= tryStart
                && d.Location.Span.Start < followingStart).ToArray();
            Assert.Single(roots);
            Assert.Contains("try", roots[0].Message, StringComparison.Ordinal);
            Assert.Contains("'{'" , roots[0].Message, StringComparison.Ordinal);
            Assert.Equal(tryStart, roots[0].Location.Span.Start);
            Assert.DoesNotContain(syntax, d => d.Location.Span.Start >= followingStart);
            Assert.Contains(document.GetSemanticTokens(), t =>
                document.Text.GetText(t.Span) == "afterProbe");
        }
    }

    [Theory]
    [InlineData("int x = 0")]
    [InlineData("double x = 0")]
    [InlineData("int x")]
    public void DeclarationWithoutSemicolonBeforeTry_PointsToDeclaration(string declaration)
    {
        var source = "void Go() {\n    " + declaration
            + "\n    try {} catch {}\n    int afterProbe = 7;\n}\n";
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var syntax = document.GetDiagnostics().Where(IsSyntax).ToArray();
            var declarationStart = source.IndexOf(declaration, StringComparison.Ordinal);
            var at = declarationStart + declaration.Length;
            var tryStart = source.IndexOf("    try", StringComparison.Ordinal) + 4;
            var roots = syntax.Where(d => d.Location.Span.Start >= declarationStart
                && d.Location.Span.Start < tryStart).ToArray();
            Assert.Single(roots);
            Assert.Contains("';'", roots[0].Message, StringComparison.Ordinal);
            if (declaration.Contains('='))
                Assert.Equal(at, roots[0].Location.Span.Start);
            else
                Assert.InRange(roots[0].Location.Span.Start, declarationStart, at);
            Assert.DoesNotContain(syntax, d => d.Location.Span.Start >= tryStart);
            Assert.Contains(document.GetSemanticTokens(), t =>
                document.Text.GetText(t.Span) == "afterProbe");
        }
    }

    [Theory]
    [InlineData("try {} catch {}")]
    [InlineData("try {} finally {}")]
    [InlineData("try\n{} catch {}")]
    [InlineData("try\n{\n}\ncatch {}")]
    public void ValidMultilineTryBodyAndHandlers_HaveNoSyntaxErrors(string statement)
    {
        var source = "void Go() {\n    " + statement
            + "\n    int afterProbe = 7;\n}\n";
        var (document, fixture) = Open(source);
        using (fixture)
        {
            Assert.Empty(document.GetDiagnostics().Where(IsSyntax));
        }
    }

    [Fact]
    public void BareTryInDestructor_DoesNotConsumeNextLoop()
    {
        var source = """
            public struct Widget { public int Value; }
            public extension Widget {
                [UnsafeBody]
                public ~Widget() {
                    try
                    while (false) {}
                    if (Value != 0) { Value = 0; }
                }
            }
            """;
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var syntax = document.GetDiagnostics().Where(IsSyntax).ToArray();
            var start = source.IndexOf("    try", StringComparison.Ordinal) + 4;
            var next = source.IndexOf("while (false)", start, StringComparison.Ordinal);
            Assert.Single(syntax.Where(d => d.Location.Span.Start >= start
                && d.Location.Span.Start < next));
            Assert.DoesNotContain(syntax, d => d.Location.Span.Start >= next);
        }
    }
}
