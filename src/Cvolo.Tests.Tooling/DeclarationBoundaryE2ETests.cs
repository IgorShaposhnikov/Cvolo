using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// End-to-end regression matrix for parser recovery: physical newlines do not end Cvolo
/// statements, but a malformed statement must not consume a subsequent independent one.
/// The complete pipeline is exercised through project opening, parsing, binding, diagnostics
/// and semantic tokens. Do not replace compiler results with parser mocks.
/// </summary>
public sealed class DeclarationBoundaryE2ETests
{
    private const string Declarations = """
        public struct Widget { public int Value; }
        public struct Box<T> { public T Value; }
        int GetValue() { return 2; }
        void Touch() { }
        """;

    // Each row: stable matrix number, preceding fragment, next fragment, expected syntax
    // root on the preceding fragment, and line delimiter. The full source is compiled as
    // a real tooling project; named cases correspond to the 60-case recovery contract.
    public static IEnumerable<object[]> StatementCases()
    {
        (int, string, string, bool, string)[] cases =
        [
            (1,  "double",             "if (ready) { y = y + 1; }", true, "\n"),
            (2,  "double x",           "if (ready) { y = y + 1; }", true, "\n"),
            (3,  "double x =",         "if (ready) { y = y + 1; }", true, "\n"),
            (4,  "double x = 0",       "if (ready) { y = y + 1; }", true, "\n"),
            (5,  "double",             "while (false) { y = y + 1; }", true, "\n"),
            (6,  "double x",           "for (int k = 0; k < 0; k = k + 1) { y = y + 1; }", true, "\n"),
            (7,  "double x =",         "foreach (val item in items) { y = y + 1; }", true, "\n"),
            (8,  "double",             "return 1;", true, "\n"),
            (9,  "double x",           "break;", true, "\n"),
            (10, "double x =",         "continue;", true, "\n"),
            (11, "double",             "switch (y) { default: y = y + 1; }", true, "\n"),
            (12, "double x",           "try { y = y + 1; } finally { y = y + 1; }", true, "\n"),
            (13, "double x =",         "defer Touch();", true, "\n"),
            (14, "double",             "unsafe { y = y + 1; }", true, "\n"),
            (15, "double",             "val later = 1;", true, "\n"),
            (16, "double x",           "var later = 1;", true, "\n"),
            (17, "double x =",         "{ 1, 2 };", false, "\n"),
            (18, "double",             "split = 1;", false, "\n"),
            (19, "double x =",         "reused = 1;", false, "\n"),
            (20, "double x",           "reused = 1;", true, "\n"),
            (21, "double x = 0",       "reused = 1;", true, "\n"),
            (22, "double x;",          "if (ready) { y = y + 1; }", false, "\n"),
            (23, "double",             "split = 1;", false, "\n"),
            (24, "double",             "split;", false, "\n"),
            (25, "double x =",         "1.5;", false, "\n"),
            (26, "double x =",         "GetValue();", false, "\n"),
            (27, "double x = 1",       "+ 2;", false, "\n"),
            (28, "double x = (",       "1 + 2 );", false, "\n"),
            (29, "double x =",         "ready ? 1 : 2;", false, "\n"),
            (30, "Widget",             "value = Widget { Value: 1 };", false, "\n"),
            (31, "Widget*",            "ptr = null;", false, "\n"),
            (32, "Widget?",            "optional;", false, "\n"),
            (33, "Widget[]",           "values;", false, "\n"),
            (34, "Box<int>",          "item;", false, "\n"),
            (35, "ref Widget",         "borrowed;", false, "\n"),
            (36, "if",                 "(ready) { y = y + 1; }", false, "\n"),
            (37, "while",              "(false) { y = y + 1; }", false, "\n"),
            (38, "widget.",            "Value = 5;", false, "\n"),
            (39, "if",                 "return 0;", true, "\n"),
            (40, "while",              "if (ready) { y = y + 1; }", true, "\n"),
            (41, "GetValue(",          "if (ready) { y = y + 1; }", true, "\n"),
            (42, "y =",                "if (ready) { y = y + 1; }", true, "\n"),
            (43, "return",             "if (ready) { y = y + 1; }", true, "\n"),
            (44, "widget.Val",         "if (ready) { y = y + 1; }", true, "\n"),
            (45, "var x =",            "while (false) { y = y + 1; }", true, "\n"),
            (46, "Widget other",       "return 0;", true, "\n"),
            (47, "y = 1 +",            "2;", false, "\n"),
            (48, "GetValue(",          "y );", false, "\n"),
            (57, "double x;",          "if (ready) { y = y + 1; }", false, " "),
            (59, "double // a comment", "if (ready) { y = y + 1; }", true, "\n"),
        ];
        foreach (var (number, before, next, invalid, separator) in cases)
            yield return [number, before, next, invalid, separator];
    }

    private static string Body(string before, string following, string separator) =>
        "bool ready = true;\n" +
        "int y = 1;\n" +
        "double reused = 0;\n" +
        "Widget widget = Widget { Value: 1 };\n" +
        before + separator + following + "\n" +
        "int afterProbe = 7;\n" +
        "return afterProbe;\n";

    private static string Wrap(string body, string context = "function")
    {
        var head = Declarations + "\n";
        return context switch
        {
            "function" => head + "int main() {\n" + body + "}\n",
            "namespace" => "namespace TestNamespace {\n" + head + "int main() {\n" + body + "}\n}\n",
            "extension" => head + "public extension Widget {\n public int Recover() {\n" + body + " }\n}\nint main() { return 0; }\n",
            "inner_if" => head + "int main() {\n if (true) {\n" + body + " }\n return 0;\n}\n",
            "inner_while" => head + "int main() {\n while (false) {\n" + body + " }\n return 0;\n}\n",
            "inner_block" => head + "int main() {\n {\n" + body + " }\n return 0;\n}\n",
            "inner_for" => head + "int main() {\n for (int i = 0; i < 1; i = i + 1) {\n" + body + " }\n return 0;\n}\n",
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };
    }

    private static bool SyntaxFault(Diagnostic diagnostic) =>
        diagnostic.Id is "CVL4173" or "CVL4174" or "CVL4175" or "CVL0000";

    private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(d => $"{d.Id}@{d.Location.Span.Start}: {d.Message}"));

    private static void AssertRecovery(string source, string before, string following,
        bool expectRoot, bool preserveTrailingCode = true)
    {
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var findings = document.GetDiagnostics();
        var faultStart = source.IndexOf(before, StringComparison.Ordinal);
        var nextStart = source.IndexOf(following, faultStart + before.Length, StringComparison.Ordinal);
        Assert.True(faultStart >= 0 && nextStart >= 0, "invalid regression fixture");

        var syntax = findings.Where(SyntaxFault).ToArray();
        if (expectRoot)
        {
            Assert.Contains(syntax, d =>
                d.Location.Span.Start >= faultStart
                && d.Location.Span.Start <= nextStart);
        }
        else
        {
            Assert.Empty(syntax);
        }

        if (preserveTrailingCode)
        {
            var probeStart = source.IndexOf("afterProbe", nextStart, StringComparison.Ordinal);
            Assert.True(probeStart >= 0, "trailing statement is missing");
            // A recovered syntax root must not generate additional syntax failures in
            // the unaffected trailing statement and the enclosing closing brace.
            Assert.DoesNotContain(syntax, d => d.Location.Span.Start >= probeStart);
            var tokens = document.GetSemanticTokens();
            Assert.Contains(tokens, t => document.Text.GetText(t.Span) == "afterProbe");
        }
    }

    [Theory]
    [MemberData(nameof(StatementCases))]
    public void StatementBoundaries_KeepNextStatement(
        int number, string before, string following, bool invalid, string separator)
    {
        var body = Body(before, following, separator);
        var source = Wrap(body);
        try
        {
            AssertRecovery(source, before, following, invalid);
        }
        catch (Exception e)
        {
            throw new Xunit.Sdk.XunitException($"Matrix #{number}: {before} / {following}\n{e.Message}");
        }
    }

    // Built-in type spellings, including void: void remains an invalid local type
    // semantically, but the parser must still recover the following independent if.
    private static readonly string[] BuiltinTypes =
    [
        "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
        "nint", "nuint", "float", "double", "bool", "string", "char", "void"
    ];

    public static IEnumerable<object[]> BuiltinCases()
    {
        foreach (var type in BuiltinTypes)
        {
            yield return [type, type, true];
            yield return [type, type + " x", true];
            yield return [type, type + " x =", true];
            yield return [type, type + " x = 1;", false];
        }
    }

    [Theory]
    [MemberData(nameof(BuiltinCases))]
    public void EveryBuiltinType_RecoversBeforeControlStatement(
        string type, string before, bool invalid)
    {
        const string next = "if (ready) { y = y + 1; }";
        var source = Wrap(Body(before, next, "\n"));
        try { AssertRecovery(source, before, next, invalid); }
        catch (Exception e)
        {
            throw new Xunit.Sdk.XunitException($"Primitive {type}: {before}\n{e.Message}");
        }
    }

    public static IEnumerable<object[]> CustomCases()
    {
        foreach (var type in new[] { "Widget", "Widget*", "Widget?", "Widget[]", "Box<int>", "ref Widget" })
        {
            yield return [type, true];
            yield return [type + " x", true];
            yield return [type + " x =", true];
            yield return [type + " x;", false];
        }
    }

    [Theory]
    [MemberData(nameof(CustomCases))]
    public void UserTypes_Pointers_Options_Arrays_Generics_AndReferences(
        string before, bool invalid)
    {
        const string next = "if (ready) { y = y + 1; }";
        var source = Wrap(Body(before, next, "\n"));
        AssertRecovery(source, before, next, invalid);
    }

    public static IEnumerable<object[]> ContextCases()
    {
        foreach (var context in new[] { "function", "extension", "namespace", "inner_if",
            "inner_while", "inner_block", "inner_for" })
        {
            yield return [context, "double", "if (ready) { y = y + 1; }", true];
            yield return [context, "double x", "if (ready) { y = y + 1; }", true];
            yield return [context, "double x =", "if (ready) { y = y + 1; }", true];
            yield return [context, "double x = 2;", "if (ready) { y = y + 1; }", false];
        }
    }

    [Theory]
    [MemberData(nameof(ContextCases))]
    public void ScopeContexts_RecoverWithoutLosingFollowingCode(
        string context, string before, string next, bool invalid)
    {
        var source = Wrap(Body(before, next, "\n"), context);
        AssertRecovery(source, before, next, invalid);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void LineEndingChanges_DoNotChangeTheRecoveredBoundary(string newLine)
    {
        const string before = "double";
        const string next = "if (ready) { y = y + 1; }";
        var source = Wrap(Body(before, next, "\n")).Replace("\n", newLine);
        AssertRecovery(source, before, next, true);
    }

    [Fact]
    public void TwoIndependentMalformedDeclarations_BothRemainDiagnosed()
    {
        var source = Wrap(
            "bool ready = true;\nint y = 1;\n" +
            "double\nif (ready) { y = y + 1; }\n" +
            "double x =\nwhile (false) { y = y + 1; }\n" +
            "int afterProbe = 7;\nreturn afterProbe;\n");
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var syntax = document.GetDiagnostics().Where(SyntaxFault).ToArray();
        var first = source.IndexOf("double", StringComparison.Ordinal);
        var second = source.IndexOf("double x =", StringComparison.Ordinal);
        Assert.Contains(syntax, d => d.Location.Span.Start >= first && d.Location.Span.Start < second);
        Assert.Contains(syntax, d => d.Location.Span.Start >= second
            && d.Location.Span.Start < source.IndexOf("while (false)", second, StringComparison.Ordinal));
        Assert.DoesNotContain(syntax, d => d.Location.Span.Start >= source.IndexOf("int afterProbe", StringComparison.Ordinal));
    }

    [Fact]
    public void IncompleteDeclarationAtEndOfFile_HasAnInRangeDiagnostic()
    {
        const string source = "int main() {\n double";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        Assert.Contains(document.GetDiagnostics(), d => SyntaxFault(d)
            && d.Location.Span.Start >= 0 && d.Location.Span.End <= source.Length);
    }

    [Fact]
    public void StructFieldRecovery_DoesNotSwallowTheNextField()
    {
        const string source = "struct Fields {\n double missing\n int afterProbe;\n}\n" +
                              "int main() { return 0; }\n";
        using var fixture = TempProject.Create(("Main.cvl", source));
        var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
        var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
        var diagnostics = document.GetDiagnostics();
        var probeAt = source.IndexOf("int afterProbe", StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => SyntaxFault(d)
            && d.Location.Span.Start < probeAt);
        Assert.DoesNotContain(diagnostics, d => SyntaxFault(d)
            && d.Location.Span.Start > probeAt + "int afterProbe;".Length);
        Assert.Contains(document.GetSemanticTokens(), t =>
            document.Text.GetText(t.Span) == "afterProbe");
    }
}
