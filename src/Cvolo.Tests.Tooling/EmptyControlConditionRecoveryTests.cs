using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class EmptyControlConditionRecoveryTests
{
    [Theory]
    [InlineData("if", "", "while (false) {}")]
    [InlineData("while", "", "while (false) {}")]
    [InlineData("if", " {}", "while (false) {}")]
    [InlineData("while", " {}", "while (false) {}")]
    [InlineData("if", "", "return;")]
    [InlineData("while", "", "return;")]
    [InlineData("if", "", "if (true) {}")]
    [InlineData("while", "", "if (true) {}")]
    public void EmptyCondition_PreservesFollowingStatement(
        string keyword, string body, string following)
    {
        var source = "void Go() {\n"
            + "    " + keyword + "()" + body + "\n"
            + "    " + following + "\n"
            + "}\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var findings = document.GetDiagnostics();
        var start = source.IndexOf(keyword + "()", StringComparison.Ordinal);
        var next = source.IndexOf(following, start + keyword.Length + 2, StringComparison.Ordinal);
        var syntax = findings.Where(d => d.Id is "CVL4173" or "CVL4174" or "CVL4175" or "CVL4004").ToArray();
        var roots = syntax.Where(d => d.Location.Span.Start >= start && d.Location.Span.Start < next).ToArray();
        Assert.Single(roots);
        Assert.Contains("condition", roots[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(start + keyword.Length + 1, roots[0].Location.Span.Start);
        Assert.DoesNotContain(syntax, d => d.Location.Span.Start >= next);
    }

    [Theory]
    [InlineData("while")]
    [InlineData("if")]
    public void EmptyBracedCondition_HasOneRootOnCondition_NotOnFollowingIf(string keyword)
    {
        var source = """
            public struct Window { public int Value; }
            public extension Window {
                [UnsafeBody]
                public ~Window() {
            """ + "\n        " + keyword + "() {}\n"
            + """
                        if (Value != 0) {
                            Value = 0;
                        }
                    }
                }
                """;
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var doc = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var diagnostics = doc.GetDiagnostics();
        var conditionStart = source.IndexOf(keyword + "()", StringComparison.Ordinal);
        var nextStart = source.IndexOf("if (Value != 0)", conditionStart, StringComparison.Ordinal);
        var syntax = diagnostics.Where(d => d.Id is "CVL4173" or "CVL4174" or "CVL4175" or "CVL4004").ToArray();
        var roots = syntax.Where(d => d.Location.Span.Start >= conditionStart && d.Location.Span.Start < nextStart).ToArray();
        Assert.Single(roots);
        Assert.Contains("condition", roots[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(conditionStart + keyword.Length + 1, roots[0].Location.Span.Start);
        Assert.DoesNotContain(syntax, d => d.Location.Span.Start >= nextStart);
    }
}
