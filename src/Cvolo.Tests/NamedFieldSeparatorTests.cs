using Cvolo.Analysis;
using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// S01-S25: brace field names accept ':' and '=' with identical semantics,
/// while contextual parenthesized fields continue to require ':'.
/// Programs are stored as standalone .cvl fixtures in TestCases.
/// </summary>
public sealed class NamedFieldSeparatorTests : CompilerTestBase
{
	private const string Folder = "NamedFieldSeparators";
	private static string Fixture(string file) => $"{Folder}/{file}.cvl";

	private static (string Source, CompilationUnitSyntax? Unit, Diagnostic[] Errors, CompilationContext Context)
		ParseFixture(string file)
	{
		var source = ReadTestCaseSource(Fixture(file));
		var context = new CompilationContext(source, file + ".cvl");
		var parser = new AntlrSyntaxParser();
		var unit = parser.Parse(context);
		return (source, unit, parser.Diagnostics.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray(), context);
	}

	private static IEnumerable<T> Descendants<T>(SyntaxNode node) where T : SyntaxNode
	{
		if (node is T typed)
			yield return typed;
		foreach (var child in node.GetChildren())
			foreach (var descendant in Descendants<T>(child))
				yield return descendant;
	}

	/// <summary>
	/// S01-S03, S05-S07, S09-S13, S16-S17, S24: both brace separators
	/// and the preexisting colon-only contextual forms must parse normally.
	/// </summary>
	[Theory]
	[InlineData("BraceColons")]
	[InlineData("BraceEquals")]
	[InlineData("BraceMixed")]
	[InlineData("HeapEquals")]
	[InlineData("ContextualColons")]
	[InlineData("ArrayReplication")]
	[InlineData("NestedContextual")]
	[InlineData("OrdinaryAssignment")]
	[InlineData("UnionColon")]
	[InlineData("UnionEquals")]
	[InlineData("OptionColon")]
	[InlineData("OptionEquals")]
	[InlineData("OptionNoneEquals")]
	[InlineData("GenericEquals")]
	[InlineData("RoundTrip")]
	[InlineData("PreserveSource")]
	[InlineData("WhereColon")]
	[InlineData("ConstructorVsContextual")]
	public void AcceptedSpelling_ParsesWithoutSyntaxErrors(string file)
	{
		var (_, unit, errors, _) = ParseFixture(file);
		Assert.NotNull(unit);
		Assert.Empty(errors);
	}

	/// <summary>
	/// S01-S03, S06, S09 and S13 must also pass semantic binding; a braced
	/// '=' must not create an assignment expression or a different field type.
	/// </summary>
	[Theory]
	[InlineData("BraceColons")]
	[InlineData("BraceEquals")]
	[InlineData("BraceMixed")]
	[InlineData("ContextualColons")]
	[InlineData("HeapEquals")]
	[InlineData("ArrayReplication")]
	[InlineData("NestedContextual")]
	[InlineData("GenericEquals")]
	[InlineData("UnionColon")]
	[InlineData("UnionEquals")]
	public void AcceptedAggregate_ResolvesTheSameFields(string file)
	{
		var (_, unit, parserErrors, context) = ParseFixture(file);
		Assert.NotNull(unit);
		Assert.Empty(parserErrors);
		var binder = new Binder();
		binder.Context.FileContexts[unit!] = context;
		binder.Bind([unit!]);
		Assert.DoesNotContain(binder.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
	}

	/// <summary>
	/// S12: Option is a standard-library union. The bare Binder fixture has no
	/// library references, so exercise both separators through the real compiler
	/// pipeline with System imported by the standalone .cvl fixtures.
	/// </summary>
	[Theory]
	[InlineData("OptionColon")]
	[InlineData("OptionEquals")]
	[InlineData("OptionNoneEquals")]
	public void OptionSeparators_CompileWithStandardLibrary(string file)
	{
		var (exitCode, stdout, stderr) = RunCompiler(Fixture(file), "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, file);
	}

	/// <summary>
	/// S01-S03: separator spelling cannot change compilation success, field
	/// evaluation order, or execution at either optimization level.
	/// </summary>
	[Theory]
	[InlineData("BraceColons", "-O0")]
	[InlineData("BraceEquals", "-O0")]
	[InlineData("BraceMixed", "-O0")]
	[InlineData("BraceColons", "-O3")]
	[InlineData("BraceEquals", "-O3")]
	[InlineData("BraceMixed", "-O3")]
	public void BraceSeparators_CompileAndExecute(string file, string optimization)
	{
		var (exitCode, stdout, stderr) = RunCompiler(Fixture(file), optimization);
		AssertCompilationSucceeded(exitCode, stdout, stderr, file);
		var (runExitCode, _) = ExecuteBinary(file, Folder);
		Assert.Equal(0, runExitCode);
	}

	/// <summary>S04: a field repeated using ':' and '=' is still a duplicate.</summary>
	[Fact]
	public void DuplicateAcrossSeparatorSpellings_IsDiagnosed()
	{
		var (_, unit, syntax, context) = ParseFixture("DuplicateMixed");
		Assert.NotNull(unit);
		Assert.Empty(syntax);
		var binder = new Binder();
		binder.Context.FileContexts[unit!] = context;
		binder.Bind([unit!]);
		Assert.Single(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.DuplicateInitializerForField);
	}

	/// <summary>S08/S25: contextual '(X = ...)' is never a named field list.</summary>
	[Theory]
	[InlineData("ParenEquals")]
	[InlineData("ParenAssignments")]
	public void EqualsInsideDeclarationParentheses_DoesNotCreateNamedFieldInitializers(string file)
	{
		var (_, unit, errors, _) = ParseFixture(file);
		// The expression alternative may fail to parse multiple assignments;
		// it must not fall back to the colon-only named-field production.
		if (unit is not null)
			Assert.DoesNotContain(Descendants<StructInitializationExpressionSyntax>(unit), _ => true);
		if (unit is null)
			Assert.NotEmpty(errors);
	}

	/// <summary>S10: ordinary assignment in parentheses is still an expression.</summary>
	[Fact]
	public void ParenthesizedAssignment_IsNotConvertedToAFieldInitializer()
	{
		var (_, unit, errors, _) = ParseFixture("OrdinaryAssignment");
		Assert.NotNull(unit);
		Assert.Empty(errors);
		Assert.DoesNotContain(Descendants<ParenthesizedStructInitializerExpressionSyntax>(unit!), _ => true);
	}

	/// <summary>S14: field values never infer omitted generic type arguments.</summary>
	[Fact]
	public void UnspecializedGeneric_RemainsInvalidWithEquals()
	{
		var (_, unit, errors, context) = ParseFixture("BareGenericEquals");
		Assert.NotNull(unit);
		Assert.Empty(errors);
		var binder = new Binder();
		binder.Context.FileContexts[unit!] = context;
		binder.Bind([unit!]);
		Assert.Contains(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.GenericInitializerRequiresTypeArguments);
		Assert.DoesNotContain(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
	}

	/// <summary>S15: '==' and '+=' receive a field-separator diagnostic, never a crash.</summary>
	[Theory]
	[InlineData("InvalidEquality", "==")]
	[InlineData("InvalidPlusAssign", "+=")]
	public void IncorrectBracedOperator_ReportsExpectedFieldSeparator(string file, string operatorText)
	{
		var (source, _, errors, _) = ParseFixture(file);
		var error = Assert.Single(errors, d => d.Id == DiagnosticIds.ExpectedToken
			&& d.Message.Contains("Expected ':' or '=' after field name", StringComparison.Ordinal));
		Assert.Equal(source.LastIndexOf(operatorText, StringComparison.Ordinal), error.Span.Start);
	}

	/// <summary>S16: printing a brace initializer canonicalizes '=', while nested parentheses stay ':'.</summary>
	[Fact]
	public void SourcePrinter_CanonicalizesBracesButPreservesContextualColons()
	{
		var (_, unit, errors, _) = ParseFixture("RoundTrip");
		Assert.NotNull(unit);
		Assert.Empty(errors);
		var initializers = Descendants<StructInitializationExpressionSyntax>(unit!).ToArray();
		var value = Assert.Single(initializers);
		var printed = CvoloSourcePrinter.Print(value);
		Assert.Contains("X = 1", printed);
		Assert.Contains("Y = 2", printed);
		Assert.Contains("BaseColor = (R: 255, G: 0, B: 0)", printed);
		var parser = new AntlrSyntaxParser();
		var roundTrip = parser.Parse(new CompilationContext(
			"public struct Color { public int R; public int G; public int B; }\n" +
			"public struct Point { public int X; public int Y; public Color BaseColor; }\n" +
			"int main() { val p = " + printed + "; return 0; }", "roundtrip.cvl"));
		Assert.NotNull(roundTrip);
		Assert.False(parser.Diagnostics.HasErrors, $"Reprinted initializer must reparse: {parser.Diagnostics}");
		Assert.Equal(value.Initializers.Select(i => i.MemberName),
			Assert.Single(Descendants<StructInitializationExpressionSyntax>(roundTrip!)).Initializers.Select(i => i.MemberName));
		var binder = new Binder();
		binder.Context.FileContexts[roundTrip!] = new CompilationContext(
			"public struct Color { public int R; public int G; public int B; }\n" +
			"public struct Point { public int X; public int Y; public Color BaseColor; }\n" +
			"int main() { val p = " + printed + "; return 0; }", "roundtrip.cvl");
		binder.Bind([roundTrip!]);
		Assert.DoesNotContain(binder.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
	}

	/// <summary>
	/// S04, S14 and S15: invalid aggregate syntax and semantic conditions must
	/// fail normally in both optimization modes without producing LLVM IR.
	/// </summary>
	[Theory]
	[InlineData("DuplicateMixed", "-O0")]
	[InlineData("DuplicateMixed", "-O3")]
	[InlineData("BareGenericEquals", "-O0")]
	[InlineData("BareGenericEquals", "-O3")]
	[InlineData("InvalidEquality", "-O0")]
	[InlineData("InvalidPlusAssign", "-O0")]
	public void InvalidSeparatorsAndGenerics_RejectCompilation(string file, string optimization)
	{
		var (exitCode, stdout, stderr) = RunCompiler(Fixture(file), optimization, "--emit-ir");
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Compile Error", stderr, StringComparison.Ordinal);
		Assert.DoesNotContain("define i32 @main", stdout, StringComparison.Ordinal);
	}

	/// <summary>S19: mixed spelling must not change the existing missing-delimiter diagnostic.</summary>
	[Theory]
	[InlineData("MixedMissingBrace", "'}'")]
	[InlineData("MixedMissingSemicolon", "';'")]
	public void MixedFields_MissingDelimiter_HasSyntaxError(string file, string closer)
	{
		var (_, _, errors, _) = ParseFixture(file);
		Assert.Contains(errors, d => d.Message.Contains(closer, StringComparison.Ordinal));
	}

	/// <summary>S20: attribute named arguments keep their colon-only grammar.</summary>
	[Theory]
	[InlineData("AttributeNamedArg")]
	[InlineData("WhereColon")]
	public void AttributeNamedArguments_RemainColonLabeled(string file)
	{
		var (_, unit, errors, _) = ParseFixture(file);
		Assert.NotNull(unit);
		Assert.Empty(errors);
	}

	/// <summary>S21/S22: invalid field type provenance is unchanged by separator spelling.</summary>
	[Theory]
	[InlineData("UnknownColorEquals")]
	[InlineData("UnknownColorColon")]
	public void UnresolvedFieldType_HasOneRootWithoutMissingFieldCascade(string file)
	{
		var (_, unit, errors, context) = ParseFixture(file);
		Assert.NotNull(unit);
		Assert.Empty(errors);
		var binder = new Binder();
		binder.Context.FileContexts[unit!] = context;
		binder.Bind([unit!]);
		Assert.Single(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.UnknownTypeOfField);
		Assert.DoesNotContain(binder.Diagnostics.Diagnostics, d => d.Id is DiagnosticIds.StructDoesNotContainField or DiagnosticIds.CannotInitializeFieldOfTypeWithValueOfType);
	}

	/// <summary>S23: genuine missing Ghost is independent of the Color declaration error.</summary>
	[Fact]
	public void RealMissingField_IsNotSuppressedByInvalidSiblingType()
	{
		var (_, unit, errors, context) = ParseFixture("IndependentGhost");
		Assert.NotNull(unit);
		Assert.Empty(errors);
		var binder = new Binder();
		binder.Context.FileContexts[unit!] = context;
		binder.Bind([unit!]);
		Assert.Single(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.UnknownTypeOfField);
		Assert.Single(binder.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.StructDoesNotContainField);
	}

	/// <summary>S24: the parser must distinguish a contextual field initializer from a constructor call.</summary>
	[Fact]
	public void ParenthesizedFields_AreNotConstructorArguments()
	{
		var (_, unit, errors, _) = ParseFixture("ConstructorVsContextual");
		Assert.NotNull(unit);
		Assert.Empty(errors);
		Assert.Single(Descendants<StructInitializationExpressionSyntax>(unit!));
		Assert.Single(Descendants<CallExpressionSyntax>(unit!));
	}
}
