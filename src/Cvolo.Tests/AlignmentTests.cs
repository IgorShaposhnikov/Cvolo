using System.Text.RegularExpressions;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class AlignmentTests : CompilerTestBase
{
	private string EmitIr(string caseName)
	{
		var (exitCode, stdout, stderr) = RunCompiler($"Alignment/{caseName}.cvl", "--emit-ir", $"-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseName);
		return stdout;
	}

	[Fact]
	public void Module_HasTargetTripleAndDataLayout()
	{
		var ir = EmitIr("Trivial");

		Assert.Contains("target datalayout =", ir);
		Assert.Contains("target triple =", ir);
	}

	[Theory]
	[InlineData("WriteLineLong")]
	[InlineData("WriteLineUlong")]
	[InlineData("WriteLineNint")]
	[InlineData("WriteLineNUint")]
	public void I64StoreLoad_UsesAlign8(string caseName)
	{
		var ir = EmitIr(caseName);

		Assert.Matches(@"store i64 [^\n]+, align 8", ir);
		Assert.Matches(@"load i64, ptr [^\n]+, align 8", ir);

		// Regression guard: no misaligned i64 anywhere
		Assert.DoesNotMatch(@"store i64 [^\n]+, align 4", ir);
		Assert.DoesNotMatch(@"load i64, ptr [^\n]+, align 4", ir);
	}

	[Theory]
	[InlineData("WriteLineInt", "i32", 4)]
	[InlineData("WriteLineShort", "i16", 2)]
	[InlineData("WriteLineDouble", "double", 8)]
	[InlineData("WriteLineByte", "i8", 1)]
	public void OtherPrimitives_UseExpectedAlignment(
		string caseName, string llvmType, int align)
	{
		var ir = EmitIr(caseName);

		Assert.Matches($@"store {llvmType} [^\n]+, align {align}", ir);
		Assert.Matches($@"load {llvmType}, ptr [^\n]+, align {align}", ir);
	}
}
