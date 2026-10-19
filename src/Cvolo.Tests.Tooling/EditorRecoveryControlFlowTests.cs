using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>Actual parser/binder/diagnostic recovery through the public Tooling workspace.</summary>
public sealed class EditorRecoveryControlFlowTests
{
    private static bool Syntax(Diagnostic d) =>
        d.Id is "CVL4004" or "CVL4173" or "CVL4174" or "CVL4175";

    private static IReadOnlyList<Diagnostic> Check(string body)
    {
        var source = "void Go() {\n    int x = 1;\n" + body + "\n    int afterProbe = 7;\n}\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        // Eagerly compute CodeLens on the damaged buffer as well.
        _ = document.GetCodeLenses();
        var result = document.GetDiagnostics();
        Assert.Contains(document.GetSemanticTokens(), t => document.Text.GetText(t.Span) == "afterProbe");
        return result;
    }

    [Theory]
    [InlineData("if (x ==) {} else {}", "expression")]
    [InlineData("while (x ==) {}", "expression")]
    [InlineData("if (x ==) {} else\nwhile (false) {}", "expression")]
    public void MissingConditionOperand_DoesNotOrphanElseOrNextStatement(string body, string message)
    {
        var errors = Check(body).Where(Syntax).ToArray();
        Assert.Single(errors);
        Assert.Contains(message, errors[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(errors, d => d.Message.Contains("else", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("try {} catch\nwhile (false) {}", "catch")]
    [InlineData("try {} catch\nif (true) {}", "catch")]
    [InlineData("try {} catch\nreturn;", "catch")]
    public void MissingCatchBody_IsOneLocalFault(string body, string message)
    {
        var errors = Check(body).Where(Syntax).ToArray();
        Assert.Single(errors);
        Assert.Contains(message, errors[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("try {} catch {} finally\\nwhile (false) {}")]
    [InlineData("try {} finally\\nif (true) {}")]
    public void MissingFinallyBlock_LeavesFollowingControlStatementIntact(string body)
    {
        var errors = Check(body).Where(Syntax).ToArray();
        Assert.Single(errors);
        Assert.Contains("finally", errors[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(errors, d =>
            d.Message.Contains("end of file", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("try {} catch {} finally {}")]
    [InlineData("try {} catch {} finally\\n{}")]
    public void FinishedFinallyBlock_IsValid(string body) =>
        Assert.Empty(Check(body).Where(Syntax));

    [Theory]
    [InlineData("if (x == 0) {} else while (false) {}")]
    [InlineData("if (x == 0) {} else\nwhile (false) {}")]
    [InlineData("if (x == 0) {} else\n{}")]
    [InlineData("try {} catch {}")]
    public void ValidControlFlow_DoesNotGetInventedErrors(string body) =>
        Assert.Empty(Check(body).Where(Syntax));
}
