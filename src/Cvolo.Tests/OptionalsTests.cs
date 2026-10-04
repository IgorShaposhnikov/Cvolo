using System.Text.RegularExpressions;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class OptionalsTests : CompilerTestBase
{
	[Theory]
	[InlineData("OptionalBasic", "42\n99\n10,20")]
	[InlineData("OptionalIsPattern", "42\nnone path\nref: 7\n7")]
	[InlineData("OptionalInterior", "5\n7\n9")]
	[InlineData("OptionalNone", "a none\nref none")]
	[InlineData("OptionalDefault", "none")]
	[InlineData("OptionalBareDefault", "0\nnone")]
	[InlineData("OptionalRefNpo", "val: 7")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"Optionals/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Optionals");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("OptionalVoidFail", "Cannot use '?' on 'void' or function types.")]
	[InlineData("OptionalNullInitFail", "null is not allowed in safe code. Use Option.None instead.")]
	[InlineData("OptionalNullAssignFail", "null is not allowed in safe code. Use Option.None instead.")]
	public void Semantic_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Optionals/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void StrictOptionFlag_RejectsOptionalSyntax()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Optionals/OptionalStrictFail.cvl", "--strict-option");

		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1100", stderr);
	}

	[Fact]
	public void StrictOptionProjectSetting_RejectsOptionalSyntax()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Optionals/OptionalStrictProj/StrictProg.cvlproj");

		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1100", stderr);
	}

	[Fact]
	public void ChainedQuestionMarks_AreRejected()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Optionals/OptionalChainFail.cvl");

		Assert.Equal(1, exitCode);
		Assert.Contains("Multiple '?' in a row are not allowed", stderr);
	}

	[Fact]
	public void RefOptional_EmitsFlatPointerLayout()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Optionals/OptionalRefNpo.cvl", "-O0", "--emit-ir");

		AssertCompilationSucceeded(exitCode, stdout, stderr, "Optionals/OptionalRefNpo.cvl");

		// 'ref Node?' lowers to a flat 8-byte pointer slot (NPO): no tag, no struct wrapper.
		Assert.Contains("%next = alloca ptr, align 8", stdout);

		// The is-pattern check is exactly one null comparison on the flat pointer.
		Assert.Equal(1, Regex.Matches(stdout, "icmp ne ptr").Count);
		Assert.DoesNotContain("union_tag_ptr", stdout);
		Assert.DoesNotContain("union_payload_ptr", stdout);
	}

	private const string TargetTypedNoneOutput =
		"a is None: 1\nc is None: 1\nb is None: 0\nb payload: 123\nnpo is None: 1\ne none";

	/// <summary>
	/// <c>Option.None</c> carries no payload type of its own, so the expected type at the use site
	/// supplies it: the '?'-declared target, the function return type, or the type flowing into a
	/// coalescing expression. Pinned at both optimization levels because the lowering materializes the
	/// Option in an entry alloca and the tagged and NPO representations take different branches.
	/// </summary>
	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void TargetTypedOptionNone_ResolvesFromSurroundingExpectedType(string optimization)
	{
		var fileName = "Optionals/OptionNoneTargetTyped.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("OptionNoneTargetTyped", "Optionals");
		Assert.Equal(0, runCode);
		Assert.Contains(TargetTypedNoneOutput, runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void TargetTypedOptionNone_LiftsPayloadFallbackIntoSome()
	{
		var fileName = "Optionals/OptionNoneTargetTyped.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (_, runStdout) = ExecuteBinary("OptionNoneTargetTyped", "Optionals");

		// 'var int? b = Option.None ?? 123;' keeps the Option layer because the declared target is an
		// Option, so the 123 fallback is wrapped in 'Some' rather than extracted. 'b is None: 0'
		// together with 'b payload: 123' is what distinguishes this from payload extraction.
		Assert.Contains("b is None: 0", runStdout.Replace("\r\n", "\n"));
		Assert.Contains("b payload: 123", runStdout.Replace("\r\n", "\n"));
	}

	[Theory]
	[InlineData("OptionNoneNoTargetFail", "var c = Option.None ?? 123;")]
	[InlineData("OptionNoneNonOptionTargetFail", "int x = Option.None;")]
	[InlineData("OptionNoneRightOperandFail", "var int? d = Option.None ?? Option.None ?? 9;")]
	public void UntypedOptionNone_IsRejected(string caseName, string offendingLine)
	{
		var fileName = $"Optionals/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains("'Option.None' requires a target Option<T> type to determine the payload type", stderr);
		Assert.Contains(offendingLine, stderr);
	}

	[Fact]
	public void CoalescingDoesNotInferOptionFromItsRightOperand()
	{
		// The fallback operator stays left-driven. With no expected type anywhere, neither the left
		// operand nor the enclosing expression may be typed by looking at the 123 on the right.
		var (exitCode, stdout, stderr) = RunCompiler("Optionals/OptionNoneNoTargetFail.cvl");

		Assert.Equal(1, exitCode);
		Assert.Contains("'Option.None' requires a target Option<T> type", stderr);
	}
}
