using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class NpoTests : CompilerTestBase
{
	[Theory]
	[InlineData("NpoFlatPtr", "Flat: 42\nNone created.")]
	[InlineData("NpoSwitchRefPromotion", "Some: 42\nNone path")]
	[InlineData("OptionMoveDtor", "Dropping: 1\nDone")]
	[InlineData("OptionDropOnReassign", "start\nDropping: 10\nmiddle\nnow: 20\nDropping: 20")]
	[InlineData("OptionMoveToParam", "in consume, some=7\nDropping: 7\nDone")]
	[InlineData("ArrayOptionZeroInit", "zero: 0")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"NPO/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "NPO");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("NpoSwitchByValueFail", "Cannot pattern-match 'Some x' by value on a nullable reference option")]
	[InlineData("NpoCastToPointerFail", "Cannot cast nullable reference option")]
	[InlineData("LargeUnionValuePassFail", "Passing by value is forbidden for unions larger than 16 bytes")]
	[InlineData("LargeUnionValueReturnFail", "Returning by value is forbidden for unions larger than 16 bytes")]
	public void Semantic_Rejections(string caseName, string expectedError)
	{
		var fileName = $"NPO/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void FlatOption_Emits_FlatPointerLayout()
	{
		var (exitCode, stdout, stderr) = RunCompiler("NPO/NpoFlatLayout.cvl", "-O0", "--emit-ir");

		AssertCompilationSucceeded(exitCode, stdout, stderr, "NPO/NpoFlatLayout.cvl");

		// Some option lives in a single flat 8-byte pointer slot (no tag/payload).
		Assert.Contains("%slot = alloca ptr, align 8", stdout);
		Assert.Contains("store ptr %payload, ptr %slot, align 8", stdout);

		// None option is a flat 8-byte pointer holding nullptr (address 0).
		Assert.Contains("%empty = alloca ptr, align 8", stdout);
		Assert.Contains("store ptr null, ptr %empty, align 8", stdout);

		// opt.Some reads the flat slot directly — the tagged-union access
		// machinery (tag/payload GEP + bitcast) must not appear.
		Assert.Contains("load ptr, ptr %slot, align 8", stdout);
		Assert.DoesNotContain("union_tag_ptr", stdout);
		Assert.DoesNotContain("union_payload_ptr", stdout);
	}
}
