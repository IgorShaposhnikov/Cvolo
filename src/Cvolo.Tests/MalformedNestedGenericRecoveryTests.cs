using System.Text.RegularExpressions;
using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

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
	[InlineData("AdjacentShift", "Pair<<", 5)]
	[InlineData("SpacedAngles", "Pair< <", 6)]
	[InlineData("SecondSlot", "Pair<int, <", 10)]
	[InlineData("EmptyArguments", "Pair<>", 5)]
	[InlineData("MissingFinalArgument", "Pair<int,>", 9)]
	[InlineData("MissingFirstArgument", "Pair<,int>", 5)]
	[InlineData("InvalidCall", "Identity<<", 9)]
	[InlineData("FieldType", "Pair< <", 6)]
	[InlineData("ReturnType", "Pair< <", 6)]
	[InlineData("ParameterType", "Pair< <", 6)]
	[InlineData("Ref", "Pair< <", 6)]
	[InlineData("RefVar", "Pair< <", 6)]
	[InlineData("Pointer", "Pair< <", 6)]
	[InlineData("Slice", "Pair< <", 6)]
	[InlineData("Optional", "Pair< <", 6)]
	[InlineData("LF", "Pair< <", 6)]
	[InlineData("CRLF", "Pair< <", 6)]
	[InlineData("Unicode", "Pair< <", 6)]
	[InlineData("EOF", "Pair< <", 6)]
	[InlineData("DeepInvalid", "Pair< <", 6)]
	[InlineData("MissingDelimiter", "Pair< <", 6)]
	[InlineData("EqualSeparator_GATED", "Pair< <", 6)]
	[InlineData("InvalidDeclaredMember", "Pair< <", 6)]
	[InlineData("InvalidTemplateStruct", "Pair< <", 6)]
	[InlineData("InvalidTemplateUnion", "Pair< <", 6)]
	public void InvalidTypeArgument_HasPrimaryDiagnosticAtSourceSlot(
		string file, string tokenSequence, int offset)
	{
		var (source, _, findings, _) = ParseCase(file);
		// Empty argument slots are invariant under formatting: whitespace can appear
		// before '>' or after '<' without changing the invalid type-argument position.
		var expectedSlot = file switch
		{
			"MissingFinalArgument" => FindMalformedSlot(source, @"Pair\s*<\s*int\s*,\s*(?<slot>>)", file),
			"MissingFirstArgument" => FindMalformedSlot(source, @"Pair\s*<\s*(?<slot>,)\s*int\s*>", file),
			_ => FindLiteralSlot(source, tokenSequence, offset, file)
		};
		var problem = Primary(findings, "type argument");
		Assert.Equal(expectedSlot, problem.Span.Start);
		Assert.InRange(problem.Span.Length, 0, 2);
		Assert.DoesNotContain(findings, d => d.Id == DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
	}

	private static int FindMalformedSlot(string source, string pattern, string file)
	{
		var matches = Regex.Matches(source, pattern);
		Assert.True(matches.Count > 0, $"Missing malformed generic slot in {file}");
		return matches[^1].Groups["slot"].Index;
	}

	private static int FindLiteralSlot(string source, string tokenSequence, int offset, string file)
	{
		var start = source.LastIndexOf(tokenSequence, StringComparison.Ordinal);
		Assert.True(start >= 0, $"Missing test token in {file}");
		return start + offset;
	}

	/// <summary>
	/// G07-G08: an unclosed outer generic list has a zero-width insertion
	/// point after the final type token, not on the initializer or return.
	/// </summary>
	[Theory]
	[InlineData("MissingCloser", "Pair<int, int")]
	[InlineData("MissingOuterCloser", "Pair<Pair<int, int>, int")]
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
		var (source, _, findings, _) = ParseCase("ExtraCloser");
		var position = source.LastIndexOf("Pair<int, int>>", StringComparison.Ordinal);
		var problem = Primary(findings, "Unexpected '>'");
		Assert.Equal(position + "Pair<int, int>".Length, problem.Span.Start);
	}

	/// <summary>
	/// G10-G13: valid nested generics bind without syntax errors, including
	/// adjacent closing angles and a comment between generic delimiters.
	/// </summary>
	[Theory]
	[InlineData("ValidNested")]
	[InlineData("ValidDeep")]
	[InlineData("TypePosition")]
	[InlineData("CommentBetweenClosers")]
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
	[InlineData("Shifts")]
	[InlineData("ShiftComparisonArguments")]
	[InlineData("Comparisons")]
	[InlineData("DeepValid")]
	public void OperatorsAndBoundedNestedTypes_RemainLegal(string file)
	{
		var (_, unit, findings, context) = ParseCase(file);
		Assert.NotNull(unit);
		Assert.DoesNotContain(findings, d => d.Severity == DiagnosticSeverity.Error);
		if (file is "Shifts" or "ShiftComparisonArguments" or "Comparisons")
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
		var (_, unit, findings, _) = ParseCase("Recovery");
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
		var (source, _, findings, _) = ParseCase("CRLF");
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
		var (_, unit, findings, context) = ParseCase("IndependentErrors");
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
		var (exitCode, stdout, stderr) = RunCompiler(Case("AdjacentShift"), optimization);
		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.ExpectedToken, stdout + stderr, StringComparison.Ordinal);
	}

	/// <summary>
	/// G10: a valid nested generic must pass the ordinary compiler check.
	/// </summary>
	[Fact]
	public void ValidNestedGeneric_PassesCompilerCheck()
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck(Case("ValidNested"));
		AssertCompilationSucceeded(exitCode, stdout, stderr, "ValidNested.cvl");
	}

	// G24 is gated until the approved braced `=` initializer grammar lands.
	// The G24 fixture is intentionally not used by a runnable theory yet.
}
