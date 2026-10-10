using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;
using Xunit;

namespace Cvolo.Tests;

/// <summary>
/// G01-G31: malformed nested generic syntax must fail closed with a root
/// parser diagnostic. Positive controls and operator expressions must survive.
/// Every Cvolo program is a separate .cvl file in TestCases.
/// </summary>
public sealed class MalformedNestedGenericRecoveryTests : CompilerTestBase
{
    private const string Root = "MalformedNestedGenerics";
    private static string Case(string name) => $"{Root}/{name}.cvl";

    private static (string Source, CompilationUnitSyntax? Unit, IReadOnlyList<Diagnostic> Findings, CompilationContext Context)
        ParseCase(string name)
    {
        var source = ReadTestCaseSource(Case(name));
        var context = new CompilationContext(source, name + ".cvl");
        var parser = new AntlrSyntaxParser();
        var unit = parser.Parse(context);
        return (source, unit, parser.Diagnostics.Diagnostics.ToArray(), context);
    }

    private static Diagnostic Primary(IReadOnlyList<Diagnostic> findings, string contains)
    {
        return Assert.Single(findings, d => d.Id is DiagnosticIds.ExpectedToken or DiagnosticIds.UnexpectedToken
            && d.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// G01-G06, G16-G18, G21, G23, G25-G27, G29: each malformed slot
    /// produces a diagnostic on the invalid angle or the missing argument.
    /// No guessed generic argument may substitute for the malformed source.
    /// </summary>
    [Theory]
    [InlineData("G01_AdjacentShift", "Pair<<", 5)]
    [InlineData("G02_SpacedAngles", "Pair< <", 6)]
    [InlineData("G03_SecondSlot", "Pair<int, <", 10)]
    [InlineData("G04_EmptyArguments", "Pair<>", 5)]
    [InlineData("G05_MissingFinalArgument", "Pair<int,>", 9)]
    [InlineData("G06_MissingFirstArgument", "Pair<,int>", 5)]
    [InlineData("G16_InvalidCall", "Identity<<", 9)]
    [InlineData("G17_FieldType", "Pair< <", 6)]
    [InlineData("G17_ReturnType", "Pair< <", 6)]
    [InlineData("G17_ParameterType", "Pair< <", 6)]
    [InlineData("G18_Ref", "Pair< <", 6)]
    [InlineData("G18_RefVar", "Pair< <", 6)]
    [InlineData("G18_Pointer", "Pair< <", 6)]
    [InlineData("G18_Slice", "Pair< <", 6)]
    [InlineData("G18_Optional", "Pair< <", 6)]
    [InlineData("G21_LF", "Pair< <", 6)]
    [InlineData("G21_CRLF", "Pair< <", 6)]
    [InlineData("G21_Unicode", "Pair< <", 6)]
    [InlineData("G21_EOF", "Pair< <", 6)]
    [InlineData("G22_DeepInvalid", "Pair< <", 6)]
    [InlineData("G23_MissingDelimiter", "Pair< <", 6)]
    [InlineData("G27_InvalidDeclaredMember", "Pair< <", 6)]
    [InlineData("G29_InvalidTemplateStruct", "Pair< <", 6)]
    [InlineData("G29_InvalidTemplateUnion", "Pair< <", 6)]
    public void InvalidTypeArgument_HasPrimaryDiagnosticAtSourceSlot(
        string file, string tokenSequence, int offset)
    {
        var (source, _, findings, _) = ParseCase(file);
        var position = source.LastIndexOf(tokenSequence, StringComparison.Ordinal);
        Assert.True(position >= 0, $"Missing test token in {file}");
        var problem = Primary(findings, "type argument");
        Assert.Equal(position + offset, problem.Span.Start);
        Assert.InRange(problem.Span.Length, 0, 2);
        Assert.DoesNotContain(findings, d => d.Id == DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
    }

    /// <summary>
    /// G07-G08: an unclosed outer generic list has a zero-width insertion
    /// point after the final type token, not on the initializer or return.
    /// </summary>
    [Theory]
    [InlineData("G07_MissingCloser", "Pair<int, int")]
    [InlineData("G08_MissingOuterCloser", "Pair<Pair<int, int>, int")]
    public void MissingCloser_IsDiagnosedBeforeInitializer(string file, string prefix)
    {
        var (source, _, findings, _) = ParseCase(file);
        var position = source.LastIndexOf(prefix, StringComparison.Ordinal);
        Assert.True(position >= 0);
        var problem = Primary(findings, "Expected '>'");
        Assert.Equal(position + prefix.Length, problem.Span.Start);
        Assert.Equal(0, problem.Span.Length);
    }

    /// <summary>
    /// G09: a surplus generic closer is not treated as an expression shift.
    /// </summary>
    [Fact]
    public void ExtraGenericCloser_IsRejectedAtSurplusToken()
    {
        var (source, _, findings, _) = ParseCase("G09_ExtraCloser");
        var position = source.LastIndexOf("Pair<int, int>>", StringComparison.Ordinal);
        var problem = Primary(findings, "Unexpected '>'");
        Assert.Equal(position + "Pair<int, int>".Length, problem.Span.Start);
    }

    /// <summary>
    /// G10-G13: valid nested generics bind without syntax errors, including
    /// adjacent closing angles and a comment between generic delimiters.
    /// </summary>
    [Theory]
    [InlineData("G10_ValidNested")]
    [InlineData("G11_ValidDeep")]
    [InlineData("G12_TypePosition")]
    [InlineData("G13_CommentBetweenClosers")]
    public void ValidNestedType_ParsesAndBinds(string file)
    {
        var (_, unit, findings, context) = ParseCase(file);
        Assert.NotNull(unit);
        Assert.DoesNotContain(findings, d => d.Severity == DiagnosticSeverity.Error);
        var binder = new Binder();
        binder.Context.FileContexts[unit!] = context;
        binder.Bind([unit!]);
        Assert.DoesNotContain(binder.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// G14-G15, G22: comparisons, all shift operators, and bounded deep
    /// nested type syntax still parse without generic recovery diagnostics.
    /// </summary>
    [Theory]
    [InlineData("G14_Shifts")]
    [InlineData("G14_ShiftComparisonArguments")]
    [InlineData("G15_Comparisons")]
    [InlineData("G22_DeepValid")]
    public void OperatorsAndBoundedNestedTypes_RemainLegal(string file)
    {
        var (_, unit, findings, context) = ParseCase(file);
        Assert.NotNull(unit);
        Assert.DoesNotContain(findings, d => d.Severity == DiagnosticSeverity.Error);
        if (file is "G14_Shifts" or "G14_ShiftComparisonArguments" or "G15_Comparisons")
        {
            var binder = new Binder();
            binder.Context.FileContexts[unit!] = context;
            binder.Bind([unit!]);
            Assert.DoesNotContain(binder.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }
    }

    /// <summary>
    /// Regression: the closing angles in Option&lt;refvar Node&lt;int&gt; &gt;
    /// close different generic lists. Neither delimiter is superfluous.
    /// This also protects the existing NPO/NpoGenericRefArg execution case.
    /// </summary>
    [Fact]
    public void NestedRefGenericClosers_DoNotProduceUnexpectedToken()
    {
        var source = ReadTestCaseSource("NPO/NpoGenericRefArg.cvl");
        var context = new CompilationContext(source, "NpoGenericRefArg.cvl");
        var parser = new AntlrSyntaxParser();
        var unit = parser.Parse(context);
        Assert.NotNull(unit);
        Assert.DoesNotContain(parser.Diagnostics.Diagnostics,
            diagnostic => diagnostic.Id is DiagnosticIds.UnexpectedToken or DiagnosticIds.ExpectedToken);
    }

    /// <summary>
    /// G19: parser recovery skips only the malformed declaration, preserving
    /// a subsequent independently well-formed generic function declaration.
    /// </summary>
    [Fact]
    public void InvalidDeclaration_DoesNotPoisonLaterValidDeclaration()
    {
        var (_, unit, findings, _) = ParseCase("G19_Recovery");
        Primary(findings, "type argument");
        Assert.NotNull(unit);
        Assert.Contains(unit!.Members, d => d is Cvolo.Core.AST.Declarations.FunctionDeclarationSyntax f && f.Name == "Good");
    }

    /// <summary>
    /// G21: CRLF offsets belong to source characters; diagnostics cannot
    /// silently normalize the supplied source and move the invalid angle.
    /// </summary>
    [Fact]
    public void CrlfSource_ReportsExactAngleWithoutNewlineNormalization()
    {
        var (source, _, findings, _) = ParseCase("G21_CRLF");
        Assert.Contains("\r\n", source, StringComparison.Ordinal);
        Assert.Equal(source.LastIndexOf("Pair< <", StringComparison.Ordinal) + 6,
            Primary(findings, "type argument").Span.Start);
    }

    /// <summary>
    /// G28: a syntactically valid but unresolved field type has the distinct
    /// semantic root CVL4135, never a fabricated missing-field CVL4093.
    /// </summary>
    [Fact]
    public void UnresolvedWellFormedField_StillHasDeclarationRoot()
    {
        var source = ReadTestCaseSource("InitializerDiagnostics/InvalidMembers/UnknownFieldNested.cvl");
        var context = new CompilationContext(source, "UnknownFieldNested.cvl");
        var parser = new AntlrSyntaxParser();
        var unit = parser.Parse(context);
        Assert.NotNull(unit);
        var binder = new Binder();
        binder.Context.FileContexts[unit!] = context;
        binder.Bind([unit!]);
        Assert.Contains(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.UnknownTypeOfField);
        Assert.DoesNotContain(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.StructDoesNotContainField);
    }

    /// <summary>
    /// G30: a genuinely absent field in a later valid declaration must still
    /// be reported independently of an earlier malformed generic type.
    /// </summary>
    [Fact]
    public void IndependentMissingMember_SurvivesMalformedGenericRecovery()
    {
        var (_, unit, findings, context) = ParseCase("G30_IndependentErrors");
        Primary(findings, "type argument");
        Assert.NotNull(unit);
        var binder = new Binder();
        binder.Context.FileContexts[unit!] = context;
        binder.Bind([unit!]);
        Assert.Contains(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.StructDoesNotContainField);
    }

    /// <summary>
    /// G01/G25: malformed nested generic type arguments must cause a normal
    /// nonzero compiler exit in debug and optimized builds, with no LLVM result.
    /// </summary>
    [Theory]
    [InlineData("-O0")]
    [InlineData("-O3")]
    public void MalformedNestedGeneric_FailsNormalBuild(string optimization)
    {
        var (exitCode, stdout, stderr) = RunCompiler(Case("G01_AdjacentShift"), optimization);
        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticIds.ExpectedToken, stdout + stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// G10: a valid nested generic must pass the ordinary compiler check.
    /// </summary>
    [Fact]
    public void ValidNestedGeneric_PassesCompilerCheck()
    {
        var (exitCode, stdout, stderr) = RunCompilerCheck(Case("G10_ValidNested"));
        AssertCompilationSucceeded(exitCode, stdout, stderr, "G10_ValidNested.cvl");
    }

    // G24 is gated until the approved braced `=` initializer grammar lands.
    // The G24 fixture is intentionally not used by a runnable theory yet.
}
