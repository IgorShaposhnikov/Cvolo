using System.Text.Json;
using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;
using Cvolo.Packaging;
using Cvolo.Syntax.Antlr;
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

		var llPath = GetCompilationArtifactPath(fileName, "obj", "Debug", "ExposeRender.ll");

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

		var llPath = GetCompilationArtifactPath(fileName, "obj", "Debug", "GraphicsCore.ll");

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
	public void ExposeExtern_BoolParams_UseNativeBoolAbi()
	{
		var fileName = $"{Category}/ExposeBoolParams.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var llPath = GetCompilationArtifactPath(fileName, "obj", "Debug", "ExposeBoolParams.ll");

		Assert.True(File.Exists(llPath), $"Expected generated LLVM IR file at '{llPath}' but it was missing.");
		var irContent = File.ReadAllText(llPath);

		// Cvolo bool has an internal/scalar LLVM value representation of i1 at the
		// native function boundary, with the target ABI's zero-extension contract.
		// Object storage is handled separately (foreign bool globals use one byte).
		Assert.Contains("@native_check_flag =", irContent);
		Assert.Contains("@native_process =", irContent);
		Assert.Contains("alias i1 (i1, i32), ptr @", irContent);
		Assert.Contains("zeroext", irContent);
	}

	[Fact]
	public void ExposeExtern_CheckedFfiBounds_GeneratesNullChecks()
	{
		var fileName = $"{Category}/ExposeBoolParams.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--checked-ffi-bounds");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var llPath = GetCompilationArtifactPath(fileName, "obj", "Debug", "ExposeBoolParams.ll");

		Assert.True(File.Exists(llPath), $"Expected generated LLVM IR file at '{llPath}' but it was missing.");
		var irContent = File.ReadAllText(llPath);

		// With --checked-ffi-bounds, exported functions with pointer params should have null checks
		Assert.Contains("ffi.trap", irContent);
		Assert.Contains("llvm.trap", irContent);
	}

	[Fact]
	public void PackageApi_RoundTrips_GenericConstraints()
	{
		const string source = """
			interface IFoo
			{
				void Bar();
			}

			interface ICompare<A>
			{
				bool Less(A other);
			}

			public struct Box<T>
				where T : IFoo
			{
				T value;
			}

			public union Maybe<T>
				where T : ICompare<T>
			{
				T inner;
			}

			public int Probe<T>(T value)
				where T : IFoo
			{
				return 1;
			}
			""";

		var unit = new AntlrSyntaxParser().Parse(new CompilationContext(source, "roundtrip.cvl"));
		var serialized = PackageApiMetadata.FromCompilationUnits(new[] { unit }).Serialize();
		var rehydrated = JsonSerializer.Deserialize<PackageApiMetadata>(serialized)!;

		Assert.Single(rehydrated.Units.SelectMany(u => u.Structs).Where(s => s.Name == "Box"));
		var box = rehydrated.Units.SelectMany(u => u.Structs).Single(s => s.Name == "Box");
		Assert.Equal(new[] { "T" }, box.GenericParameters);
		Assert.Equal(new List<string> { "IFoo" }, box.GenericParameterConstraints["T"]);

		var maybe = rehydrated.Units.SelectMany(u => u.Unions).Single(s => s.Name == "Maybe");
		Assert.Equal(new[] { "T" }, maybe.GenericParameters);
		Assert.Equal(new List<string> { "ICompare<T>" }, maybe.GenericParameterConstraints["T"]);

		var probe = rehydrated.Units.SelectMany(u => u.Functions).Single(f => f.Name == "Probe");
		Assert.Equal(new[] { "T" }, probe.GenericParameters);
		Assert.Equal(new List<string> { "IFoo" }, probe.GenericParameterConstraints["T"]);

		// The rehydrated declarations carry the same generic parameters and constraints.
		var rebuild = rehydrated.CreateCompilationUnits("pkg", "1.0.0");
		var boxDecl = rebuild
			.SelectMany(u => (u.NamespaceDeclaration?.Members ?? u.Members).OfType<StructDeclarationSyntax>())
			.Single(s => s.Name == "Box");
		Assert.Equal(new List<string> { "IFoo" }, boxDecl.GenericParameterConstraints["T"]);

		var probeDecl = rebuild
			.SelectMany(u => (u.NamespaceDeclaration?.Members ?? u.Members).OfType<FunctionDeclarationSyntax>())
			.Single(f => f.Name == "Probe");
		Assert.Equal(new[] { "T" }, probeDecl.GenericParameters);
		Assert.Equal(new List<string> { "IFoo" }, probeDecl.GenericParameterConstraints["T"]);
	}
}
