using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Regression for editor recovery inside a real destructor, not merely in main().
/// Every incomplete declaration must have exactly one syntax root on its own line.
/// </summary>
public sealed class DestructorDeclarationRecoveryTests
{
    [Theory]
    [InlineData("int")]
    [InlineData("int x")]
    [InlineData("int x =")]
    [InlineData("int x = 1")]
    [InlineData("double")]
    [InlineData("double x")]
    [InlineData("double x =")]
    [InlineData("double x = 1")]
    [InlineData("Widget")]
    [InlineData("Widget x")]
    [InlineData("Widget x =")]
    [InlineData("Widget x = Widget { Value: 1 }")]
    public void IncompleteLocalBeforeIf_ProducesOneRootAndPreservesIf(string fragment)
    {
        var source = """
            public struct Widget { public int Value; }
            public extension Widget {
                [UnsafeBody]
                public ~Widget() {
            """ + "\n" + "        " + fragment + "\n"
            + """
                        if (Value != 0) {
                            Value = 0;
                        }
                    }
                }
                """;
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var diagnostics = document.GetDiagnostics();
        var declarationStart = source.IndexOf("        " + fragment + "\n", StringComparison.Ordinal);
        var nextStart = source.IndexOf("if (Value != 0)", declarationStart, StringComparison.Ordinal);
        Assert.True(declarationStart >= 0 && nextStart > declarationStart);
        // Missing semicolon diagnostics are zero-width at the end of the
        // declaration. They still belong to that declaration, not to 'if'.
        var roots = diagnostics.Where(d =>
            d.Id is "CVL4173" or "CVL4174" or "CVL4175" or "CVL4004"
            && d.Location.Span.Start >= declarationStart
            && d.Location.Span.Start < nextStart).ToArray();
        Assert.True(roots.Length == 1,
            $"'{fragment}': expected exactly one root, got {roots.Length}: "
            + string.Join(" | ", diagnostics.Select(d => $"{d.Id} at {d.Location.Span.Start}: {d.Message}")));
        Assert.DoesNotContain("before 'if'", roots[0].Message, StringComparison.OrdinalIgnoreCase);
        var expectedStart = fragment.EndsWith("=", StringComparison.Ordinal)
            ? declarationStart + "        ".Length + fragment.Length - 1
            : fragment.Contains(" = ", StringComparison.Ordinal)
                ? declarationStart + "        ".Length + fragment.LastIndexOf(' ') + 1
                : fragment.EndsWith(" x", StringComparison.Ordinal)
                    ? declarationStart + "        ".Length + fragment.Length - 1
                    : declarationStart + "        ".Length;
        Assert.Equal(expectedStart, roots[0].Location.Span.Start);

        Assert.DoesNotContain(diagnostics, d =>
            d.Id is "CVL4173" or "CVL4174" or "CVL4175" or "CVL4004"
            && d.Location.Span.Start >= nextStart);
    }
}
