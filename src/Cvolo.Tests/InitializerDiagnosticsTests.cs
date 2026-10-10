using Xunit;
using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// Compiler-level regression tests for the initializer diagnostic/recovery contract.
/// Every input program lives in TestCases/InitializerDiagnostics (not in C# strings).
/// Parser, binder, compiler check, and selected -O0/-O3 builds are tested independently.
/// </summary>
public sealed class InitializerDiagnosticsTests : CompilerTestBase
{
    private const string Root = "InitializerDiagnostics";

    private static string Case(string relative) => $"{Root}/{relative}";

    private static (string Source, IReadOnlyList<Diagnostic> Diagnostics) ParseCase(string relative)
    {
        var source = ReadTestCaseSource(Case(relative));
        var parser = new AntlrSyntaxParser();
        parser.Parse(new CompilationContext(source, relative));
        return (source, parser.Diagnostics.Diagnostics.ToArray());
    }

    private static IReadOnlyList<Diagnostic> BindCases(params string[] cases)
    {
        var binder = new Binder();
        var units = new List<CompilationUnitSyntax>();
        foreach (var relative in cases)
        {
            var parser = new AntlrSyntaxParser();
            var context = new CompilationContext(ReadTestCaseSource(Case(relative)), relative);
            var unit = parser.Parse(context);
            Assert.NotNull(unit);
            Assert.DoesNotContain(parser.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            binder.Context.FileContexts[unit!] = context;
            units.Add(unit!);
        }

        binder.Bind(units);
        return binder.Diagnostics.Diagnostics.ToArray();
    }

    private static Diagnostic Only(IReadOnlyList<Diagnostic> diagnostics, string id) =>
        Assert.Single(diagnostics, d => d.Id == id);

    private static void NoDependentErrors(IReadOnlyList<Diagnostic> diagnostics) =>
        Assert.DoesNotContain(diagnostics, d => d.Id is
            DiagnosticIds.StructDoesNotContainField or
            DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType or
            DiagnosticIds.MissingInitializerForFieldOfStruct);

    /// <summary>
    /// P01: reports a missing initializer brace at the insertion offset after the previous value, rather than on return.
    /// </summary>
    [Fact]
    public void MissingBrace_BeforeReturn_IsReportedAtInitializerEnd()
    {
        var (source, diagnostics) = ParseCase("Syntax/MissingInitializerBrace.cvl");
        var problem = Assert.Single(diagnostics, d => d.Id == DiagnosticIds.ExpectedToken
            && d.Message.Contains("Expected '}' to close struct initializer", StringComparison.Ordinal));
        // The .cvl fixture documents "Value2: 2" before the actual expression.
        // Anchor the insertion position to the last occurrence in source code, not to that comment.
        Assert.Equal(source.LastIndexOf("Value2: 2", StringComparison.Ordinal) + "Value2: 2".Length,
            problem.Span.Start);
        Assert.Equal(0, problem.Span.Length);
        Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.UnexpectedToken
            && d.Message.Contains("return", StringComparison.Ordinal));
    }

    /// <summary>
    /// P02: LF and CRLF fixtures both insert a missing semicolon immediately after
    /// the preceding declaration, never at the next return statement.
    /// The CRLF case additionally verifies that original line endings survive parsing.
    /// </summary>
    [Theory]
    [InlineData("Syntax/MissingSemicolon.cvl", "X: 1 }", false)]
    [InlineData("Syntax/MissingSemicolonCRLF.cvl", "    int x = 1", true)]
    public void MissingSemicolon_IsReportedAtPreviousDeclarationEnd(
        string relative, string previousText, bool expectsCrLf)
    {
        var (source, diagnostics) = ParseCase(relative);
        if (expectsCrLf)
            Assert.Contains("\r\n", source, StringComparison.Ordinal);

        var problem = Assert.Single(diagnostics, d => d.Id == DiagnosticIds.ExpectedToken
            && d.Message.Contains("Expected ';' after variable declaration", StringComparison.Ordinal));
        // Anchor to the final source declaration, not any documentation comment.
        var previousStart = source.LastIndexOf(previousText, StringComparison.Ordinal);
        Assert.True(previousStart >= 0, $"Missing expected declaration ending in {relative}");
        // The insertion is immediately after the last token, before CRLF, not after the newline.
        Assert.Equal(previousStart + previousText.Length, problem.Span.Start);
        Assert.Equal(0, problem.Span.Length);
    }

    /// <summary>
    /// P03: an unclosed initializer at EOF produces a zero-width insertion diagnostic, never a parser crash.
    /// </summary>
    [Fact]
    public void MissingBrace_AtEOF_DoesNotCrashOrHighlightFictionalNextToken()
    {
        var (source, diagnostics) = ParseCase("Syntax/MissingInitializerBraceAtEOF.cvl");
        Assert.Contains(diagnostics, d => d.Id == DiagnosticIds.ExpectedToken
            && d.Message.Contains("Expected '}' to close struct initializer", StringComparison.Ordinal)
            && d.Span.Start <= source.Length && d.Span.Length == 0);
    }

    /// <summary>
    /// P04: a declaration ending at EOF must diagnose the absent semicolon at source.Length.
    /// </summary>
    [Fact]
    public void MissingSemicolon_AtEOF_HasItsOwnZeroWidthInsertionFinding()
    {
        var (source, diagnostics) = ParseCase("Syntax/MissingSemicolonAtEOF.cvl");
        var finding = Assert.Single(diagnostics, d => d.Id == DiagnosticIds.ExpectedToken
            && d.Message.Contains("Expected ';' after variable declaration", StringComparison.Ordinal));
        Assert.Equal(source.Length, finding.Span.Start);
        Assert.Equal(0, finding.Span.Length);
    }

    /// <summary>
    /// P11: valid return syntax must not trigger missing/extra token diagnostics.
    /// </summary>
    [Fact]
    public void ValidReturn_HasNoDelimiterError()
    {
        var (_, diagnostics) = ParseCase("Syntax/ValidReturn.cvl");
        Assert.DoesNotContain(diagnostics, d => d.Id is DiagnosticIds.ExpectedToken
            or DiagnosticIds.UnexpectedToken or DiagnosticIds.UnexpectedEndOfFile);
    }

    /// <summary>
    /// P06/T13: a bare Pair initializer requires generic type arguments and must not invent conversion errors.
    /// </summary>
    [Fact]
    public void BareGenericPair_RequiresTypeArgumentsWithoutFieldConversions()
    {
        var diagnostics = BindCases("Generic/BarePair.cvl");
        Only(diagnostics, DiagnosticIds.GenericInitializerRequiresTypeArguments);
        Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
    }

    /// <summary>
    /// P07: a correctly constructed generic initializer has no semantic errors and passes compiler check.
    /// </summary>
    [Fact]
    public void ConstructedGenericPair_WithCorrectTypes_IsAccepted()
    {
        Assert.DoesNotContain(BindCases("Generic/CorrectPair.cvl"), d => d.Severity == DiagnosticSeverity.Error);
        var (exitCode, stdout, stderr) = RunCompilerCheck(Case("Generic/CorrectPair.cvl"));
        AssertCompilationSucceeded(exitCode, stdout, stderr, "Generic/CorrectPair.cvl");
    }

    /// <summary>
    /// P07: valid generic initialization compiles under both -O0 and -O3 and runs successfully.
    /// </summary>
    [Theory]
    [InlineData("-O0")]
    [InlineData("-O3")]
    public void ValidConstructedPair_BuildsAndRuns(string optimization)
    {
        var fixture = Case("Generic/CorrectPair.cvl");
        var (exitCode, stdout, stderr) = RunCompiler(fixture, optimization);
        AssertCompilationSucceeded(exitCode, stdout, stderr, fixture);
        var (runtimeCode, _) = ExecuteBinary("CorrectPair", Case("Generic"));
        Assert.Equal(0, runtimeCode);
    }

    /// <summary>
    /// P08/T11: a single incompatible initializer produces exactly one CVL4019
    /// for the offending member, both for generic and ordinary struct fields.
    /// </summary>
    [Theory]
    [InlineData("Generic/WrongValue2.cvl", "Value2")]
    [InlineData("InvalidMembers/BadNormalField.cvl", "B")]
    public void OneWrongField_ReportsExactlyOneConversionForThatMember(string relative, string member)
    {
        var diagnostics = BindCases(relative);
        var error = Only(diagnostics, DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
        Assert.Contains($"'{member}'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// P09: two independent wrong values produce two separate CVL4019 positions.
    /// </summary>
    [Fact]
    public void TwoWrongGenericFields_KeepTwoIndependentConversions()
    {
        var diagnostics = BindCases("Generic/TwoWrongValues.cvl");
        var conversions = diagnostics.Where(d => d.Id == DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType).ToArray();
        Assert.Equal(2, conversions.Length);
        Assert.Equal(2, conversions.Select(d => d.Span.Start).Distinct().Count());
        Assert.Contains(conversions, d => d.Message.Contains("Value1", StringComparison.Ordinal));
        Assert.Contains(conversions, d => d.Message.Contains("Value2", StringComparison.Ordinal));
    }

    /// <summary>
    /// T21: unresolved generic argument types do not cascade into field-conversion errors.
    /// </summary>
    [Fact]
    public void UnknownGenericTypeArgument_DoesNotProduceFabricatedFieldConversions()
    {
        var diagnostics = BindCases("Generic/UnknownTypeArgument.cvl");
        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
    }

    /// <summary>
    /// T02/T03/T05/T09/T15: an invalid declared field keeps one CVL4135 root
    /// without a fake missing member, missing initializer or conversion error.
    /// The specialized generic struct case must obey this same invariant.
    /// </summary>
    [Theory]
    [InlineData("InvalidMembers/UnknownFieldNested.cvl")]
    [InlineData("InvalidMembers/UnknownFieldBraced.cvl")]
    [InlineData("InvalidMembers/UnknownFieldUninitialized.cvl")]
    [InlineData("InvalidMembers/RepeatedUse.cvl")]
    [InlineData("InvalidMembers/UnknownGenericStructField.cvl")]
    public void UnknownFieldType_EmitsOneRootWithoutMemberOrConversionCascade(string relative)
    {
        var diagnostics = BindCases(relative);
        Only(diagnostics, DiagnosticIds.UnknownTypeOfField);
        NoDependentErrors(diagnostics);
    }

    /// <summary>
    /// T02: the single CVL4135 span covers the original unknown type token at the declaration.
    /// </summary>
    [Fact]
    public void UnknownDeclaredFieldType_AnchorsOriginalTypeToken()
    {
        const string relative = "InvalidMembers/UnknownFieldNested.cvl";
        var source = ReadTestCaseSource(Case(relative));
        var finding = Only(BindCases(relative), DiagnosticIds.UnknownTypeOfField);
        Assert.Equal(source.IndexOf("Color BaseColor", StringComparison.Ordinal), finding.Span.Start);
        Assert.Equal("Color".Length, finding.Span.Length);
    }

    /// <summary>
    /// T04: a truly absent member keeps CVL4093 even alongside another invalid-typed member.
    /// </summary>
    [Fact]
    public void RealMissingMember_IsNotSuppressedByUnrelatedInvalidFieldType()
    {
        var diagnostics = BindCases("InvalidMembers/IndependentMissingMember.cvl");
        Only(diagnostics, DiagnosticIds.UnknownTypeOfField);
        var missing = Only(diagnostics, DiagnosticIds.StructDoesNotContainField);
        Assert.Contains("OtherColor", missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
    }

    /// <summary>
    /// T06: two distinct unresolved field declarations must emit two distinct CVL4135 roots.
    /// </summary>
    [Fact]
    public void TwoIndependentBadFieldDeclarations_RetainTwoRootErrors()
    {
        var diagnostics = BindCases("InvalidMembers/TwoUnknownDeclarations.cvl");
        Assert.Equal(2, diagnostics.Count(d => d.Id == DiagnosticIds.UnknownTypeOfField));
    }

    /// <summary>
    /// T07: an unknown declared type cannot conceal a duplicate field declaration.
    /// </summary>
    [Fact]
    public void DuplicateDeclaration_DetectedEvenWhenItsTypeIsUnknown()
    {
        var diagnostics = BindCases("InvalidMembers/DuplicateInvalidDeclaration.cvl");
        Only(diagnostics, DiagnosticIds.UnknownTypeOfField);
        Only(diagnostics, DiagnosticIds.DuplicateFieldInStruct);
    }

    /// <summary>
    /// T08/T10: a declared union variant with an unknown payload type is not
    /// treated as an absent variant, including after generic specialization.
    /// One declaration error must not fabricate initializer conversion errors.
    /// </summary>
    [Theory]
    [InlineData("InvalidMembers/UnknownVariant.cvl")]
    [InlineData("InvalidMembers/UnknownGenericUnionVariant.cvl")]
    public void UnknownUnionVariantType_DoesNotBecomeAbsentVariant(string relative)
    {
        var diagnostics = BindCases(relative);
        Only(diagnostics, DiagnosticIds.UnknownTypeOfFieldInUnion);
        Assert.DoesNotContain(diagnostics, d => d.Id is DiagnosticIds.UnionDoesNotContainVariant
            or DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
    }

    /// <summary>
    /// T16 analogue: genuine duplicate initializer remains visible alongside unresolved type root.
    /// </summary>
    [Fact]
    public void IndependentDuplicateInitializer_RemainsVisibleAlongsideUnknownTypeRoot()
    {
        var diagnostics = BindCases("InvalidMembers/IndependentDuplicateInitializer.cvl");
        Only(diagnostics, DiagnosticIds.UnknownTypeOfField);
        Only(diagnostics, DiagnosticIds.DuplicateInitializerForField);
        NoDependentErrors(diagnostics);
    }

    /// <summary>
    /// T12: independently wrong resolved field value still reports CVL4019 next to CVL4135.
    /// </summary>
    [Fact]
    public void IndependentConversion_RemainsVisibleAlongsideUnknownTypeRoot()
    {
        var diagnostics = BindCases("InvalidMembers/IndependentBadConversion.cvl");
        Only(diagnostics, DiagnosticIds.UnknownTypeOfField);
        var mismatch = Only(diagnostics, DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
        Assert.Contains("Flag", mismatch.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.StructDoesNotContainField);
    }

    /// <summary>
    /// T17/T26: a resolved nested type accepts normal contextual initialization without diagnostics.
    /// </summary>
    [Theory]
    [InlineData("InvalidMembers/ValidNestedTargetType.cvl")]
    [InlineData("InvalidMembers/ForwardDeclaredColor.cvl")]
    public void ResolvedNestedFields_AreAcceptedNormally(string relative)
    {
        var diagnostics = BindCases(relative);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// T14: the missing-required-member rule must keep working for entirely valid field types.
    /// </summary>
    [Fact]
    public void OrdinaryMissingRequiredField_IsStillDiagnosed()
    {
        var diagnostics = BindCases("InvalidMembers/MissingRequiredField.cvl");
        Only(diagnostics, DiagnosticIds.MissingInitializerForFieldOfStruct);
    }

    /// <summary>
    /// T05/T23: a declaration with an invalid member emits one root regardless of input-file order.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidDeclarationUsedAcrossFiles_ProducesOneRootIndependentOfUnitOrder(bool reverse)
    {
        var files = new[] { "MultiFile/UnknownField/Types.cvl", "MultiFile/UnknownField/Main.cvl" };
        if (reverse)
            Array.Reverse(files);
        var diagnostics = BindCases(files);
        Only(diagnostics, DiagnosticIds.UnknownTypeOfField);
        NoDependentErrors(diagnostics);
    }

    /// <summary>
    /// T26: forward type resolution works regardless of source-file enumeration order.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidForwardDeclarationAcrossFiles_WorksInEitherUnitOrder(bool reverse)
    {
        var files = new[] { "MultiFile/ForwardColor/Types.cvl", "MultiFile/ForwardColor/Color.cvl", "MultiFile/ForwardColor/Main.cvl" };
        if (reverse)
            Array.Reverse(files);
        var diagnostics = BindCases(files);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// T01/T13/T23: a semantic initializer error must reject normal compilation
    /// and report its own root diagnostic, regardless of the error category.
    /// </summary>
    [Theory]
    [InlineData("InvalidMembers/UnknownFieldNested.cvl", DiagnosticIds.UnknownTypeOfField)]
    [InlineData("Generic/BarePair.cvl", DiagnosticIds.GenericInitializerRequiresTypeArguments)]
    public void InvalidInitializer_RejectsNormalBuildWithRootDiagnostic(string relative, string diagnosticId)
    {
        var (exitCode, _, stderr) = RunCompiler(Case(relative), "-O0");
        Assert.NotEqual(0, exitCode);
        Assert.Contains(diagnosticId, stderr, StringComparison.Ordinal);
    }
}
