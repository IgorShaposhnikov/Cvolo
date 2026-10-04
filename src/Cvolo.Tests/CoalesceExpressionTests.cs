using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// Regressions for the Option fallback (coalescing) operator <c>??</c>. The INC requires the
/// operator to require an <c>Option&lt;T&gt;</c> left operand with no implicit lifting; to prefer a
/// bare payload over an <c>Option</c> fallback and so yield either <c>T</c> or <c>Option&lt;T&gt;</c>;
/// to handle exactly one Option layer; to right-associate; to semantically check both operands
/// while emitting the right one only on the runtime <c>None</c> path; and to stay available under
/// <c>--strict-option</c>, which disables only the <c>T?</c> type sugar. Every runtime case is
/// pinned at both -O0 and -O3.
/// </summary>
public sealed class CoalesceExpressionTests : CompilerTestBase
{
	private const string Category = "Coalesce";
	private const string LeftMessage = "Coalescing operator requires Option<T> on the left operand; found '";
	private const string RightMessage = "Right operand is not compatible with coalescing payload type '";
	private const string AssignMessage = "The fallback assignment operator '??=' is not supported.";

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void ExtractMode_YieldsBarePayloadAndSkipsRightOperandWhenLeftIsSome(string optimization)
	{
		// Extract mode: a bare payload fallback unwraps the Option, and the marker proves the
		// right operand is only emitted on the None path.
		AssertRunsAndPrints("ExtractBasic", "a=42", optimization);
		AssertRunsAndPrints("ExtractBasic", "b=7", optimization);
		AssertRunsAndPrints("ExtractBasic", "c=42", optimization);
		AssertRunsAndPrints("ExtractBasic", "d=-1", optimization);

		var (_, stdout) = ExecuteBinary("ExtractBasic", $"{Category}/ExtractBasic");
		var lines = SplitLines(stdout);

		// Exactly one SIDE-EFFECT line, and it sits between c=42 and d=-1: the Some path above it
		// never evaluates the fallback and the None path below it always does.
		var marker = lines.IndexOf("SIDE-EFFECT");
		Assert.True(marker > 0, $"SIDE-EFFECT missing from output:\n{stdout}");
		Assert.Equal("c=42", lines[marker - 1]);
		Assert.Equal("d=-1", lines[marker + 1]);
		Assert.Equal(1, lines.Count(l => l == "SIDE-EFFECT"));
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void PreserveMode_YieldsOptionSoItUnwrapsAgain(string optimization)
	{
		// An Option fallback keeps the Option layer: `someValue ?? noValue` and `noValue ?? someValue`
		// both need a second `??` to read, while a bare payload fallback takes priority and extracts.
		AssertRunsAndPrints("PreserveOptional", "a=42", optimization);
		AssertRunsAndPrints("PreserveOptional", "b=42", optimization);
		AssertRunsAndPrints("PreserveOptional", "c=7", optimization);
		AssertRunsAndPrints("PreserveOptional", "d=42", optimization);
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void Chain_IsRightAssociative(string optimization)
	{
		// The fixture only compiles under right-associativity: `a ?? (b ?? c)` keeps the inner
		// result an Option<int>, whereas `(a ?? b) ?? c` would make it a bare int and trip CVL4185.
		AssertRunsAndPrints("AssociativityChain", "chained=99", optimization);
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void NpoRepresentation_TestsPointerAgainstNullAndMovesFallbackOption(string optimization)
	{
		// Option<ref int> is a bare `i8*` where null means None, so the discriminator is a null
		// comparison rather than a loaded tag. The None-path fallback moves the whole Option.
		AssertRunsAndPrints("NpoReferencePayload", "viaSome=7", optimization);
		AssertRunsAndPrints("NpoReferencePayload", "viaNone=7", optimization);
		AssertRunsAndPrints("NpoReferencePayload", "guarded=7", optimization);

		var (_, stdout) = ExecuteBinary("NpoReferencePayload", $"{Category}/NpoReferencePayload");
		Assert.Single(SplitLines(stdout).Where(l => l == "SIDE-EFFECT"));
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void StructPayload_LoadsInlineAggregate(string optimization)
	{
		AssertRunsAndPrints("StructPayload", "a=1,2", optimization);
		AssertRunsAndPrints("StructPayload", "b=40,50", optimization);
		AssertRunsAndPrints("StructPayload", "c=1,2", optimization);
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void LeftOperand_IsEvaluatedExactlyOnce(string optimization)
	{
		// The left value is needed both to test the discriminant and to read the payload; a call
		// with a visible side effect must therefore print its marker exactly once per coalesce.
		var (_, stdout) = ExecuteBinaryAfterCompile("LeftEvaluatedOnce", "LeftEvaluatedOnce", optimization);
		var lines = SplitLines(stdout);

		Assert.Equal(new[] { "COUNTED", "a=5", "COUNTED-NONE", "b=7" }, lines);
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void StrictOption_KeepsFallbackOperatorWhileDisablingTypeSugar(string optimization)
	{
		// --strict-option disables the `T?` sugar only. This fixture spells every Option type out
		// explicitly, so it must still compile and run with the flag on.
		var folder = $"{Category}/StrictOptionExplicitTypes";
		var (exitCode, stdout, stderr) = RunCompiler(folder, optimization, "--strict-option");
		AssertCompilationSucceeded(exitCode, stdout, stderr, folder);

		var (runCode, runStdout) = ExecuteBinary("StrictOptionExplicitTypes", folder);
		Assert.Equal(0, runCode);
		Assert.Equal(new[] { "a=42", "b=7", "c=42" }, SplitLines(runStdout));
	}

	[Fact]
	public void PlainLeftOperand_IsRejectedWithOptionRequired()
	{
		var fileName = $"{Category}/LeftNotOptionFail.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.CoalesceLeftOperandMustBeOptionFound, stderr);
		Assert.Contains(LeftMessage, stderr);

		// No implicit lifting, so the reported type is the plain int operand.
		Assert.Contains("found 'int'", stderr);
	}

	[Fact]
	public void IncompatibleRightOperand_IsRejectedAndNamesThePayloadType()
	{
		var fileName = $"{Category}/RightNotCompatibleFail.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.CoalesceRightOperandNotCompatibleWithPayloadTypeOf, stderr);
		Assert.Contains(RightMessage, stderr);
		Assert.Contains("int'", stderr);
	}

	[Fact]
	public void AssignOperator_IsRejectedWithAnExplicitDiagnostic()
	{
		var fileName = $"{Category}/AssignOperatorFail.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.CoalesceAssignIsNotSupported, stderr);
		Assert.Contains(AssignMessage, stderr);
	}

	/// <summary>
	/// The payload conversion is tried before the Option conversion, so the two halves of the matrix
	/// bind differently: a bare <c>T</c> fallback unwraps to <c>T</c>, an <c>Option&lt;T&gt;</c> fallback
	/// keeps the layer and yields an <c>Option&lt;T&gt;</c>. Both left shapes (Some and None) are accepted,
	/// so only the right operand selects the mode.
	/// </summary>
	[Fact]
	public void Descriptor_PayloadFallbackExtracts_OptionFallbackPreserves()
	{
		var descriptors = AnalyzeDescriptors("DescriptorMatrix");

		Assert.Equal(4, descriptors.Count);
		Assert.All(descriptors, d => Assert.True(d.IsSuccess));

		var extract = descriptors.Take(2).ToList();
		Assert.All(extract, d =>
		{
			Assert.Equal(CoalesceResultMode.ExtractPayloadOrFallback, d.Mode);
			Assert.Equal(CoalesceRightConversion.Payload, d.RightConversion);
			Assert.False(d.ProducesOptional);
			Assert.False(d.ProducesOptionalOnNonePath);
		});

		var preserve = descriptors.Skip(2).ToList();
		Assert.All(preserve, d =>
		{
			Assert.Equal(CoalesceResultMode.PreserveOptionalOrFallback, d.Mode);
			Assert.Equal(CoalesceRightConversion.Optional, d.RightConversion);
			Assert.True(d.ProducesOptional);
		});

		// Extract mode erases the Option, preserve mode hands back the left operand's Option type.
		Assert.All(extract, d => Assert.False(IsOption(d.ResultType)));
		Assert.All(preserve, d => Assert.True(IsOption(d.ResultType)));
	}

	/// <summary>
	/// <c>Option&lt;ref T&gt;</c> is lowered as a bare pointer, so the descriptor has to mark the
	/// fallback as using the NPO representation rather than a loaded discriminant.
	/// </summary>
	[Fact]
	public void Descriptor_ReferencePayloadUsesNpoRepresentation()
	{
		var descriptors = AnalyzeDescriptors("NpoReferencePayload");

		Assert.NotEmpty(descriptors);
		Assert.All(descriptors, d =>
		{
			Assert.True(d.IsSuccess);
			Assert.True(d.UsesNpoRepresentation);
		});
	}

	/// <summary>
	/// A fallback chain handles exactly one Option layer. The inner <c>b ?? c</c> extracts to a bare
	/// <c>int</c>, but the outer node's left operand is the original <c>Option&lt;int&gt;</c>, so nothing
	/// is recursively flattened and the chain reports no coalescing diagnostic.
	/// </summary>
	[Fact]
	public void Descriptor_ChainHandlesExactlyOneOptionLayer()
	{
		var (diagnostics, descriptors) = AnalyzeCase("AssociativityChain");

		Assert.DoesNotContain(diagnostics, d => d.Id is DiagnosticIds.CoalesceLeftOperandMustBeOptionFound
			or DiagnosticIds.CoalesceRightOperandNotCompatibleWithPayloadTypeOf);
		Assert.NotEmpty(descriptors);
	}

	/// <summary>
	/// An Option fallback of a different payload type is not convertible: no widening, no implicit
	/// lift. CVL4186 names the left payload type.
	/// </summary>
	[Fact]
	public void MismatchedOptionPayload_IsRejectedByTheBinder()
	{
		var (diagnostics, _) = AnalyzeCase("RightNotCompatibleFail.cvl");

		Assert.Contains(diagnostics, d => d.Id == DiagnosticIds.CoalesceRightOperandNotCompatibleWithPayloadTypeOf);
	}

	/// <summary>
	/// Focused binder assertion for the left-operand rule, independent of the CLI. A plain value is
	/// not implicitly lifted, so CVL4185 is the coalescing diagnostic reported.
	/// </summary>
	[Fact]
	public void LeftOperandRule_ReportsCoalesceDiagnosticIdOnly()
	{
		var (diagnostics, _) = AnalyzeCase("LeftNotOptionFail.cvl");

		Assert.Contains(diagnostics, d => d.Id == DiagnosticIds.CoalesceLeftOperandMustBeOptionFound);
		Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.CoalesceRightOperandNotCompatibleWithPayloadTypeOf);
	}

	/// <summary>
	/// The fallback operator is scoped to <c>Option&lt;T&gt;</c>. A raw pointer and a native delegate are
	/// both null-comparable, but neither is an Option and neither is lifted to one, so each is
	/// rejected rather than given null-coalescing semantics it does not otherwise have.
	/// </summary>
	[Theory]
	[InlineData("RawPointerLeftFail.cvl", "int*")]
	[InlineData("NativeDelegateLeftFail.cvl", "Callback")]
	public void NullComparableNonOptionLeftOperand_IsNotLifted(string fixture, string reportedType)
	{
		var fileName = $"{Category}/{fixture}";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.CoalesceLeftOperandMustBeOptionFound, stderr);
		Assert.Contains($"found '{reportedType}'", stderr);
	}

	/// <summary>
	/// The parser produces a right-nested tree. A left-nested tree would put a coalesce on the left
	/// child of the outermost node, which the grammar cannot produce on its own because the expression
	/// rule is left-recursive.
	/// </summary>
	[Fact]
	public void Parser_BuildsRightNestedTree()
	{
		var coalesce = ParseFirstCoalesce(
			"int Read(Option<int> a, Option<int> b, Option<int> c) { var chained = a ?? b ?? c; return 0; }");

		Assert.NotNull(coalesce);
		Assert.IsNotType<CoalesceExpressionSyntax>(coalesce!.Left);
		Assert.IsType<CoalesceExpressionSyntax>(coalesce.Right);
		Assert.Equal(SyntaxKind.CoalesceExpression, coalesce.Kind);

		var inner = Assert.IsType<CoalesceExpressionSyntax>(coalesce.Right);
		Assert.IsNotType<CoalesceExpressionSyntax>(inner.Left);
		Assert.IsNotType<CoalesceExpressionSyntax>(inner.Right);
	}

	/// <summary>
	/// A parenthesized chain keeps the grouping the author wrote, so the operator is not flattened
	/// across explicit parentheses.
	/// </summary>
	[Fact]
	public void Parser_ParenthesesOverrideAssociativity()
	{
		var coalesce = ParseFirstCoalesce(
			"int Read(Option<int> a, Option<int> b, Option<int> c) { var chained = (a ?? b) ?? c; return 0; }");

		Assert.NotNull(coalesce);
		Assert.IsType<CoalesceExpressionSyntax>(coalesce!.Left);
		Assert.IsNotType<CoalesceExpressionSyntax>(coalesce.Right);
	}

	[Fact]
	public void SourcePrinter_RoundTripsTheOperator()
	{
		var (_, unit) = Parse("int Read(Option<int> a, Option<int> b) { var value = a ?? b; return value ?? -1; }");
		var printed = CvoloSourcePrinter.Print(unit);

		Assert.Contains("a ?? b", printed);
		Assert.Contains("value ?? -1", printed);
	}

	/// <summary>
	/// The fallback operator keeps its own token, so a chained optional type in type position no
	/// longer lexes as two `?` tokens but must still produce the existing CVL1101 diagnostic.
	/// </summary>
	[Fact]
	public void ChainedQuestionMarksInTypePosition_StillReportOptionalTypeChain()
	{
		var diagnostics = ParseDiagnostics("int main() { var int?? x = 5; return 0; }");

		Assert.Contains(diagnostics, d => d.Id == DiagnosticIds.OptionalTypeChainForbidden);
	}

	private static (IReadOnlyList<Diagnostic> Diagnostics, CompilationUnitSyntax Unit) Parse(string source)
	{
		var parser = new AntlrSyntaxParser();
		var unit = parser.Parse(new CompilationContext(source, "Coalesce.cvl"));
		Assert.NotNull(unit);
		return (parser.Diagnostics.Diagnostics, unit!);
	}

	private static List<Diagnostic> ParseDiagnostics(string source) => Parse(source).Diagnostics.ToList();

	private static CoalesceExpressionSyntax? ParseFirstCoalesce(string source) => FindCoalesce(Parse(source).Unit);

	/// <summary>
	/// Binds a fixture through the full project pipeline so the base library is loaded and
	/// <c>Option&lt;T&gt;</c> resolves, then returns its diagnostics alongside the cached descriptors
	/// in source order.
	/// </summary>
	private (List<Diagnostic> Diagnostics, List<ResolvedCoalesceExpression> Descriptors) AnalyzeCase(string sourcePath)
	{
		var (_, context) = AnalyzeProject($"{Category}/{sourcePath}");

		var descriptors = context.ResolvedCoalesceExpressions.Values
			.OrderBy(d => d.Syntax.Span.Start)
			.ToList();

		return (context.Diagnostics.Diagnostics.ToList(), descriptors);
	}

	private List<ResolvedCoalesceExpression> AnalyzeDescriptors(string sourcePath)
	{
		var (diagnostics, descriptors) = AnalyzeCase(sourcePath);

		Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

		return descriptors;
	}

	private static bool IsOption(TypeSymbol? type) => type is UnionTypeSymbol { IsOption: true };

	/// <summary>Returns the outermost fallback expression in pre-order, which is the chain root.</summary>
	private static CoalesceExpressionSyntax? FindCoalesce(SyntaxNode root)
	{
		foreach (var node in Descendants(root))
		{
			if (node is CoalesceExpressionSyntax coalesce)
				return coalesce;
		}

		return null;
	}

	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
	{
		foreach (var child in node.GetChildren())
		{
			yield return child;
			foreach (var grandchild in Descendants(child))
				yield return grandchild;
		}
	}

	private static List<string> SplitLines(string text) =>
		text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

	private (int ExitCode, string StdOut) ExecuteBinaryAfterCompile(string binaryName, string caseName, string optimization)
	{
		var folder = $"{Category}/{caseName}";
		var (exitCode, stdout, stderr) = RunCompiler(folder, optimization);
		AssertCompilationSucceeded(exitCode, stdout, stderr, folder);
		return ExecuteBinary(binaryName, folder);
	}

	private void AssertRunsAndPrints(string caseName, string expected, string optimization)
	{
		var folder = $"{Category}/{caseName}";
		var (exitCode, stdout, stderr) = RunCompiler(folder, optimization);
		AssertCompilationSucceeded(exitCode, stdout, stderr, folder);

		var (runCode, runStdout) = ExecuteBinary(caseName, folder);
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout.Replace("\r\n", "\n"));
	}
}
