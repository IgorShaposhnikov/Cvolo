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
/// Regressions for optional chaining <c>?.</c> and the synthesized delegate <c>Invoke</c> member.
/// The INC requires <c>?.</c> to be scoped to <c>Option&lt;T&gt;</c> with no implicit lifting; to
/// evaluate the receiver exactly once and emit the member and its arguments only on the runtime
/// <c>Some</c> path; to lift the member result into exactly one synthetic <c>Option</c> layer
/// (never flattening an already-Option member); to keep a <c>void</c> member <c>void</c>; to expose
/// <c>Delegate.Invoke</c> as a compiler-owned member that normalizes to the same call; and to stay
/// available under <c>--strict-option</c>, which disables only the <c>T?</c> type sugar. Runtime
/// cases are pinned at both -O0 and -O3.
/// </summary>
public sealed class OptionalChainingTests : CompilerTestBase
{
	private const string Category = "OptionalChaining";
	private const string ReceiverMessage = "Optional chaining requires Option<T> as the receiver; found '";
	private const string WritebackMessage = "An optional-chained member is not an assignment target;";
	private const string InvokeReservedMessage = "'Invoke' is the compiler-owned delegate invocation member.";

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void FieldAndMethod_ProjectThroughThePayload(string optimization)
	{
		// A field read and an extension-method call both resolve against the payload; None on either
		// short-circuits to absence.
		var (_, stdout) = RunAndCapture("Basic", optimization);
		Assert.Equal(new[] { "field=12", "method=12", "none=None", "noneMethod=None" }, SplitLines(stdout));
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void DelegateInvoke_NormalizesToTheDelegateCall(string optimization)
	{
		// `?.Invoke` is the synthesized delegate member: it lowers to the same delegate call, and the
		// delegate's return value is lifted exactly one layer on the Some path.
		var (_, stdout) = RunAndCapture("DelegateInvoke", optimization);
		Assert.Equal(new[] { "invoke=42", "absent=None" }, SplitLines(stdout));
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void Arguments_AreCheckedAlwaysButEvaluatedOnlyOnSome(string optimization)
	{
		// The argument has a visible side effect. It runs once (the Some path) and never for None,
		// even though it is semantically checked for both.
		var (_, stdout) = RunAndCapture("NoneSkipsArguments", optimization);
		var lines = SplitLines(stdout);
		Assert.Equal(new[] { "ARG-EVALUATED", "some=6", "none=None" }, lines);
		Assert.Single(lines.Where(l => l == "ARG-EVALUATED"));
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void Receiver_IsEvaluatedExactlyOnce(string optimization)
	{
		// The receiver call needs to both test the discriminant and project the payload; a call with a
		// visible side effect must therefore print its marker exactly once.
		var (_, stdout) = RunAndCapture("ReceiverEvaluatedOnce", optimization);
		var lines = SplitLines(stdout);
		Assert.Equal(new[] { "RECEIVER-CALLED", "read=7" }, lines);
		Assert.Single(lines.Where(l => l == "RECEIVER-CALLED"));
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void OptionMember_IsNotFlattened(string optimization)
	{
		// `Maybe` returns Option<int>, so `present?.Maybe(...)` is Option<Option<int>>. The helpers read
		// each layer: Some-of-Some yields 9 and Some-of-None yields the inner sentinel -1, whereas a
		// collapsed layer would surface the outer absence sentinel -2.
		var (_, stdout) = RunAndCapture("OneLayerLift", optimization);
		Assert.Equal(new[] { "some=9", "none=-1" }, SplitLines(stdout));
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void VoidMember_StaysVoidAndRunsOnlyOnSome(string optimization)
	{
		// A void member produces no value: the Some path runs its side effect and no Option is built;
		// None skips the call entirely.
		var (_, stdout) = RunAndCapture("VoidMember", optimization);
		Assert.Equal(new[] { "fire 3 value=7", "done" }, SplitLines(stdout));
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void StrictOption_KeepsOperatorWhileDisablingTypeSugar(string optimization)
	{
		// --strict-option disables the `T?` sugar only. This fixture spells every Option type out, so
		// it must still compile and run with the flag on.
		var folder = $"{Category}/StrictOptionExplicitTypes";
		var (exitCode, stdout, stderr) = RunCompiler(folder, optimization, "--strict-option");
		AssertCompilationSucceeded(exitCode, stdout, stderr, folder);

		var (runCode, runStdout) = ExecuteBinary("StrictOptionExplicitTypes", folder);
		Assert.Equal(0, runCode);
		Assert.Equal(new[] { "field=12", "method=12" }, SplitLines(runStdout));
	}

	[Fact]
	public void PlainReceiver_IsRejectedWithOptionRequired()
	{
		var (exitCode, _, stderr) = RunCompiler($"{Category}/PlainReceiverFail");

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.OptionalChainReceiverMustBeOptionFound, stderr);
		Assert.Contains(ReceiverMessage, stderr);

		// No implicit lifting, so the reported type is the plain receiver.
		Assert.Contains("found 'int'", stderr);
	}

	[Fact]
	public void ConditionalWriteback_IsRejectedAsNonAssignable()
	{
		var (exitCode, _, stderr) = RunCompiler($"{Category}/WritebackFail");

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.OptionalChainIsNotAssignable, stderr);
		Assert.Contains(WritebackMessage, stderr);
	}

	[Fact]
	public void UserExtensionInvokeOnDelegate_IsRejected()
	{
		var (exitCode, _, stderr) = RunCompiler($"{Category}/ExtensionInvokeFail");

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.ExtensionInvokeOnDelegateTypeIsReserved, stderr);
		Assert.Contains(InvokeReservedMessage, stderr);
	}

	/// <summary>
	/// Binds a fixture through the full project pipeline so the base library and <c>Option&lt;T&gt;</c>
	/// resolve, then returns its diagnostics alongside the cached descriptors in source order.
	/// </summary>
	private (List<Diagnostic> Diagnostics, List<ResolvedOptionalChainExpression> Descriptors) AnalyzeCase(string caseName)
	{
		var (_, context) = AnalyzeProject($"{Category}/{caseName}");

		var descriptors = context.ResolvedOptionalChains.Values
			.OrderBy(d => d.Syntax.Span.Start)
			.ToList();

		return (context.Diagnostics.Diagnostics.ToList(), descriptors);
	}

	/// <summary>
	/// Every chain in the basic fixture binds successfully, and each result is an <c>Option</c> of the
	/// bare member type - a field and an extension method, each on Some and None.
	/// </summary>
	[Fact]
	public void Descriptor_SuccessfulChainsLiftIntoSingleOption()
	{
		var (_, descriptors) = AnalyzeCase("Basic");

		Assert.Equal(4, descriptors.Count);
		Assert.All(descriptors, d =>
		{
			Assert.True(d.IsSuccess);
			Assert.True(IsOption(d.ResultType));
			Assert.Equal("Option<int>", d.ResultType!.Name);
			Assert.False(d.UsesNpoRepresentation);
		});

		Assert.Equal(OptionalChainMemberKind.Field, descriptors[0].MemberKind);
		Assert.Equal(OptionalChainMemberKind.Method, descriptors[1].MemberKind);
		Assert.Equal("id", descriptors[0].Syntax.MemberName);
		Assert.Equal("GetId", descriptors[1].Syntax.MemberName);
		Assert.NotNull(descriptors[1].Method);
	}

	/// <summary>
	/// A member that already returns <c>Option&lt;int&gt;</c> makes the chain result
	/// <c>Option&lt;Option&lt;int&gt;&gt;</c>: the binder adds one layer rather than flattening.
	/// </summary>
	[Fact]
	public void Descriptor_OptionMemberGainsOneLayer()
	{
		var (_, descriptors) = AnalyzeCase("OneLayerLift");

		Assert.Equal(2, descriptors.Count);
		Assert.All(descriptors, d =>
		{
			Assert.True(d.IsSuccess);
			Assert.True(IsOption(d.ResultType));
			Assert.True(IsOption(d.MemberType));
		});
	}

	/// <summary>
	/// The parser produces a field form for <c>x?.field</c> and a call form for <c>x?.M()</c> and
	/// <c>x?.M(a, b)</c>; a zero-argument call is still a call, not a field read.
	/// </summary>
	[Fact]
	public void Parser_DistinguishesFieldAndInvocationForms()
	{
		var field = ParseFirstOptionalChain(ReadTestCaseSource($"{Category}/Syntax/Field.cvl"));
		Assert.NotNull(field);
		Assert.Equal("value", field!.MemberName);
		Assert.False(field.IsInvocation);
		Assert.Null(field.Arguments);

		var empty = ParseFirstOptionalChain(ReadTestCaseSource($"{Category}/Syntax/EmptyInvocation.cvl"));
		Assert.NotNull(empty);
		Assert.True(empty!.IsInvocation);
		Assert.Empty(empty.Arguments!);

		var withArgs = ParseFirstOptionalChain(ReadTestCaseSource($"{Category}/Syntax/InvocationArgs.cvl"));
		Assert.NotNull(withArgs);
		Assert.True(withArgs!.IsInvocation);
		Assert.Equal(2, withArgs.Arguments!.Count);
	}

	[Fact]
	public void SourcePrinter_RoundTripsBothForms()
	{
		var (_, unit) = Parse(ReadTestCaseSource($"{Category}/Syntax/FieldAndMethod.cvl"));
		var printed = CvoloSourcePrinter.Print(unit);

		Assert.Contains("o?.value", printed);
		Assert.Contains("o?.Bump(1)", printed);
	}

	private static (IReadOnlyList<Diagnostic> Diagnostics, CompilationUnitSyntax Unit) Parse(string source)
	{
		var parser = new AntlrSyntaxParser();
		var unit = parser.Parse(new CompilationContext(source, "OptionalChaining.cvl"));
		Assert.NotNull(unit);
		return (parser.Diagnostics.Diagnostics, unit!);
	}

	private static OptionalChainExpressionSyntax? ParseFirstOptionalChain(string source) => FindOptionalChain(Parse(source).Unit);

	private static bool IsOption(TypeSymbol? type) => type is UnionTypeSymbol { IsOption: true };

	private static OptionalChainExpressionSyntax? FindOptionalChain(SyntaxNode root)
	{
		foreach (var node in Descendants(root))
		{
			if (node is OptionalChainExpressionSyntax chain)
				return chain;
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

	private (int ExitCode, string StdOut) RunAndCapture(string caseName, string optimization)
	{
		var folder = $"{Category}/{caseName}";
		var (exitCode, stdout, stderr) = RunCompiler(folder, optimization);
		AssertCompilationSucceeded(exitCode, stdout, stderr, folder);
		return ExecuteBinary(caseName, folder);
	}
}
