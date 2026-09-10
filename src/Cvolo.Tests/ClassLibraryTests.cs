using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ClassLibraryTests : CompilerTestBase
{
	private const string Category = "ExposeExtern";
	[Fact]
	public void ClassLibrary_NoMain_cvlo5001_Reported()
	{
		var (exitCode, stdout, stderr) = RunCompiler("ClassLibrary/NoMain.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains("CVL5001", stderr);
	}

	[Fact]
	public void ClassLibrary_NoMain_SkippedForSharedLibrary()
	{
		var (exitCode, stdout, stderr) = RunCompiler("ClassLibrary/NoMain.cvl", "--shared");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "ClassLibrary/NoMain.cvl");
	}

	[Fact]
	public void ExposeExtern_SspStrong_AppliedToExportedFunctions()
	{
		var fileName = "ExposeExtern/ExposeRender.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var assemblyDir = Path.GetDirectoryName(typeof(ClassLibraryTests).Assembly.Location)!;
		var llPath = Path.Combine(assemblyDir, "TestCases", "_isolated", "ExposeExtern", "ExposeRender", "obj", "Debug", "ExposeRender.ll");

		Assert.True(File.Exists(llPath), $"Expected generated LLVM IR file at '{llPath}' but it was missing.");
		var irContent = File.ReadAllText(llPath);

		// ExposeRender has an expose extern function 'RenderFrame'. Verify sspstrong is applied.
		Assert.Contains("sspstrong", irContent);
	}

	[Fact]
	public void ExposeExtern_VisibilityHidden_ForLibraryBuild()
	{
		// Compile as shared library and verify -fvisibility=hidden is effective
		// by checking the IR has protected visibility on the alias
		var fileName = "InteropCSharp/GraphicsCore.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--shared");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var assemblyDir = Path.GetDirectoryName(typeof(ClassLibraryTests).Assembly.Location)!;
		var llPath = Path.Combine(assemblyDir, "TestCases", "_isolated", "InteropCSharp", "GraphicsCore", "obj", "Debug", "GraphicsCore.ll");

		Assert.True(File.Exists(llPath), $"Expected generated LLVM IR file at '{llPath}' but it was missing.");
		var irContent = File.ReadAllText(llPath);

		// Library builds with -fvisibility=hidden should only expose symbols via dllexport/protected aliases
		if (OperatingSystem.IsWindows())
			Assert.Contains("dllexport alias", irContent);
		else
			Assert.Contains("protected alias", irContent);
	}

	[Theory]
	[InlineData("ExposeBoolParams", "1\n42\n")]
	public void ExposeExtern_BoolParams_CompileAndRun(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void ExposeExtern_BoolParams_LoweredToI8()
	{
		var fileName = $"{Category}/ExposeBoolParams.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var assemblyDir = Path.GetDirectoryName(typeof(ClassLibraryTests).Assembly.Location)!;
		var llPath = Path.Combine(assemblyDir, "TestCases", "_isolated", Category, "ExposeBoolParams", "obj", "Debug", "ExposeBoolParams.ll");

		Assert.True(File.Exists(llPath), $"Expected generated LLVM IR file at '{llPath}' but it was missing.");
		var irContent = File.ReadAllText(llPath);

		// Bool parameters crossing the FFI boundary should be lowered to i8 (1-byte C ABI bool)
		// Check that the exposed alias type signatures use i8 for bool params
		Assert.Contains("@native_check_flag =", irContent);
		Assert.Contains("@native_process =", irContent);

		// The export alias should use i8 for bool parameters, not i1
		// native_check_flag(bool, int) -> i8 (i8, i32)
		Assert.Contains("alias i8 (i8, i32), ptr @", irContent);
	}

	[Fact]
	public void ExposeExtern_CheckedFfiBounds_GeneratesNullChecks()
	{
		var fileName = $"{Category}/ExposeBoolParams.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--checked-ffi-bounds");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var assemblyDir = Path.GetDirectoryName(typeof(ClassLibraryTests).Assembly.Location)!;
		var llPath = Path.Combine(assemblyDir, "TestCases", "_isolated", Category, "ExposeBoolParams", "obj", "Debug", "ExposeBoolParams.ll");

		Assert.True(File.Exists(llPath), $"Expected generated LLVM IR file at '{llPath}' but it was missing.");
		var irContent = File.ReadAllText(llPath);

		// With --checked-ffi-bounds, exported functions with pointer params should have null checks
		Assert.Contains("ffi.trap", irContent);
		Assert.Contains("llvm.trap", irContent);
	}
}
