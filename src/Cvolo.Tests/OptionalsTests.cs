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
}