using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class AttributesTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnsafeBodyOnFunction", "Answer: 42")]
	[InlineData("UnsafeBodyOnConstructor", "Level: 9")]
	[InlineData("UnsafeBodyOnDestructor", "Closing session 7")]
	[InlineData("UnsafeBodyOnHeapAlloc", "First: 42")]
	public void Execution(string caseName, string expected)
	{
		var fileName = $"Attributes/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Attributes");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void UnsafeBody_NoEffect_Should_Warn_But_Still_Compile_And_Run()
	{
		var fileName = "Attributes/UnsafeBodyNoEffectWarn.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(0, exitCode);
		Assert.Contains("Analysis Warning CVL1001", stderr);
		Assert.Contains("'[UnsafeBody]' attribute has no effect because function contains no unsafe operations.", stderr);

		var (runCode, runStdout) = ExecuteBinary("UnsafeBodyNoEffectWarn", "Attributes");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 42", runStdout);
	}

	[Fact]
	public void Nowarn_Flag_Suppresses_Warning()
	{
		var fileName = "Attributes/UnsafeBodyNoEffectWarn.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--nowarn", "CVL1001");

		Assert.Equal(0, exitCode);
		Assert.DoesNotContain("has no effect", stderr);

		var (runCode, runStdout) = ExecuteBinary("UnsafeBodyNoEffectWarn", "Attributes");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 42", runStdout);
	}

	[Fact]
	public void Nowarn_List_With_Unknown_Ids_Still_Suppresses_Match()
	{
		var fileName = "Attributes/UnsafeBodyNoEffectWarn.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName, "--nowarn", "CVL9999,cvl1001");

		Assert.Equal(0, exitCode);
		Assert.DoesNotContain("has no effect", stderr);
	}

	[Fact]
	public void Nowarn_Wrong_Id_Keeps_Warning()
	{
		var fileName = "Attributes/UnsafeBodyNoEffectWarn.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName, "--nowarn", "CVL9999");

		Assert.Equal(0, exitCode);
		Assert.Contains("'[UnsafeBody]' attribute has no effect because function contains no unsafe operations.", stderr);
	}

	[Fact]
	public void Unknown_Attribute_Warns_But_Still_Compiles_And_Runs()
	{
		var fileName = "Attributes/UnknownAttrWarn.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(0, exitCode);
		Assert.Contains("Analysis Warning CVL1002", stderr);
		Assert.Contains("Unknown attribute 'Bogus'; it will be ignored.", stderr);

		var (runCode, runStdout) = ExecuteBinary("UnknownAttrWarn", "Attributes");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 5", runStdout);
	}

	[Fact]
	public void Unknown_Attribute_Suppressed_Per_Declaration_Regardless_Of_Order()
	{
		var fileName = "Attributes/UnknownAttrSuppressed.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.Equal(0, exitCode);
		Assert.DoesNotContain("Unknown attribute", stderr);

		var (runCode, runStdout) = ExecuteBinary("UnknownAttrSuppressed", "Attributes");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 5", runStdout);
	}

	[Theory]
	[InlineData("InlineOnFunction", "Answer: 42")]
	[InlineData("NeverInlineOnFunction", "Answer: 12")]
	[InlineData("InlineOnMethod", "Answer: 42")]
	public void InlineAttributes_Compile_And_Run(string caseName, string expected)
	{
		var fileName = $"Attributes/Inlines/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Attributes/Inlines");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void Inline_Emits_AlwaysInline_Attribute_In_IR()
	{
		var fileName = "Attributes/Inlines/InlineOnFunction.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.Contains("""attributes #0 = { "alwaysinline" }""", stdout.Replace("\r\n", "\n"));
	}

	[Fact]
	public void NeverInline_Emits_NoInline_Attribute_In_IR()
	{
		var fileName = "Attributes/Inlines/NeverInlineOnFunction.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.Contains("""attributes #0 = { "noinline" }""", stdout.Replace("\r\n", "\n"));
	}

	[Fact]
	public void Inline_On_Recursive_Function_Warns_But_Still_Runs()
	{
		var fileName = "Attributes/Inlines/InlineOnRecursiveWarn.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(0, exitCode);
		Assert.Contains("Analysis Warning CVL1401", stderr);
		Assert.Contains("Function 'Fib' is recursive; LLVM may ignore the '[Inline]' hint.", stderr);

		var (runCode, runStdout) = ExecuteBinary("InlineOnRecursiveWarn", "Attributes/Inlines");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 5", runStdout);
	}

	[Fact]
	public void Inline_And_NeverInline_Conflict_Fails()
	{
		var fileName = "Attributes/Inlines/InlineConflictFail.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains("Attribute '[Inline]' cannot be combined with the other inlining attribute on the same declaration.", stderr);
	}

	[Fact]
	public void Tbaa_Scalar_Field_Access_Emits_Tags_In_IR()
	{
		var fileName = "Attributes/Tbaa/TbaaScalarField.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.Contains("!tbaa", stdout.Replace("\r\n", "\n"));

		(exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		var (runCode, runStdout) = ExecuteBinary("TbaaScalarField", "Attributes/Tbaa");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 9", runStdout);
	}

	[Fact]
	public void Tbaa_Refvar_Access_Is_Suppressed_In_IR()
	{
		var fileName = "Attributes/Tbaa/TbaaRefvarSuppressed.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("!tbaa", stdout.Replace("\r\n", "\n"));

		(exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		var (runCode, runStdout) = ExecuteBinary("TbaaRefvarSuppressed", "Attributes/Tbaa");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 43", runStdout);
	}

	[Fact]
	public void Tbaa_UnsafeBody_Is_Suppressed_In_IR()
	{
		var fileName = "Attributes/Tbaa/TbaaUnsafeSuppressed.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("!tbaa", stdout.Replace("\r\n", "\n"));

		(exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		var (runCode, runStdout) = ExecuteBinary("TbaaUnsafeSuppressed", "Attributes/Tbaa");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 1", runStdout);
	}

	[Fact]
	public void Tbaa_Struct_With_Ref_Field_Is_Suppressed_In_IR()
	{
		var fileName = "Attributes/Tbaa/TbaaRefFieldSuppressed.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("!tbaa", stdout.Replace("\r\n", "\n"));

		(exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		var (runCode, runStdout) = ExecuteBinary("TbaaRefFieldSuppressed", "Attributes/Tbaa");
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 3", runStdout);
	}

	[Theory]
	[InlineData("NoAliasOnFunctionFail", "Attribute '[NoAlias]' cannot be applied in Safe context.")]
	[InlineData("ParamAttrContextFail", "Attribute '[NoAlias]' cannot be applied in Safe context.")]
	[InlineData("UnsafeBodyOnStructFail", "Attribute '[UnsafeBody]' cannot be applied to struct declarations.")]
	[InlineData("DuplicateAttrFail", "Duplicate attribute '[UnsafeBody]'.")]
	[InlineData("SuppressWarningBadArgFail", "Attribute '[SuppressWarning]' requires exactly one string literal argument.")]
	[InlineData("SuppressWarningUnknownIdFail", "Unknown warning id 'BogusWarning'.")]
	public void Safety_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Attributes/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
