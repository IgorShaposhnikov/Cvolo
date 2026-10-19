using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

namespace Cvolo.Tests.Tooling;

public sealed class EditorRecoveryKeywordTests
{
    private static (DocumentSnapshot Document, TempProject Fixture) Open(string source)
    {
        var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        return (project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
    }

    [Theory]
    [InlineData("for", "while (false) {}")]
    [InlineData("foreach", "while (false) {}")]
    [InlineData("defer", "while (false) {}")]
    [InlineData("heap", "while (false) {}")]
    [InlineData("for", "foreach (val item in items) {}")]
    public void IncompleteKeyword_DoesNotConsumeFollowingStatement(string fragment, string following)
    {
        var source = "void Go() {\n" + fragment + "\n" + following + "\nint afterProbe = 7;\n}\n";
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var syntax = document.GetDiagnostics().Where(d =>
                d.Id is "CVL4004" or "CVL4173" or "CVL4174" or "CVL4175" or "CVL1065").ToArray();
            Assert.Contains(syntax, d => d.Location.Span.Start <= source.IndexOf(fragment, StringComparison.Ordinal)
                && d.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(syntax, d =>
                d.Location.Span.Start >= source.IndexOf(following, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("try {} ca|", "catch")]
    [InlineData("try {} fi|", "finally")]
    [InlineData("try {} |", "catch")]
    [InlineData("try {} catch {} fi|", "finally")]
    [InlineData("try {} catch {} ca|", "catch")]
    public void HandlerCompletion_OffersCompilerKeyword(string fragment, string expected)
    {
        var cursor = fragment.IndexOf('|');
        var source = "void Go() {\n" + fragment.Remove(cursor, 1) + "\n}\n";
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var results = document.GetCompletions("void Go() {\n".Length + cursor);
            Assert.Contains(results.Candidates, c => c.Label == expected && c.Kind == CompletionKind.Keyword);
        }
    }

    [Fact]
    public void HandlerCompletion_DoesNotLeakIntoOrdinaryBlock()
    {
        const string source = "void Go() { if (true) {} ca }\n";
        var (document, fixture) = Open(source);
        using (fixture)
        {
            var result = document.GetCompletions(source.IndexOf("ca }", StringComparison.Ordinal) + 2);
            Assert.DoesNotContain(result.Candidates, c => c.Label == "catch");
        }
    }

    [Theory]
    [InlineData("void x;", true)]
    [InlineData("void* x;", false)]
    [InlineData("int x;", false)]
    public void VoidValueType_IsRejectedWithoutRejectingPointers(string declaration, bool invalid)
    {
        var (document, fixture) = Open("void Go() {\n" + declaration + "\n}\n");
        using (fixture)
            Assert.Equal(invalid, document.GetDiagnostics().Any(d => d.Id == "CVL4220"));
    }
}
