using Cvolo.Analysis.Symbols;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class FFITests : CompilerTestBase
{
	[Theory]
	[InlineData("FFI/UnknownConvention.cvl",
		"Compile Error CVL1700",
		"Unknown calling convention 'fastcall'")]
	[InlineData("FFI/LibraryImportOnStandaloneExtern.cvl",
		"Compile Error CVL1701",
		"Attribute '[LibraryImport]' can only be applied to an extern block.")]
	[InlineData("FFI/ImportNameOutsideBlock.cvl",
		"Compile Error CVL1702",
		"Attribute '[ImportName]' can only be applied to a function declaration inside an extern block.")]
	[InlineData("FFI/LibraryImportOnBlockFunction.cvl",
		"Compile Error CVL1703",
		"Attribute '[LibraryImport]' attaches a library to an extern block, not to an individual function inside it.")]
	public void ExternBlock_Diagnostics_Are_Reported(string caseFile, string idText, string message)
	{
		var (exitCode, stdout, stderr) = RunCompiler(caseFile);
		Assert.Equal(1, exitCode);
		Assert.Contains(idText, stderr);
		Assert.Contains(message, stderr);
	}

	[Fact]
	public void NamedArgs_RegisterPlatformPaths()
	{
		// Named win:/linux:/mac: paths are honored unconditionally: they are stored on the
		// NativeLibraryInfo record and forwarded to the linker for the matching --target OS.
		var (exitCode, stdout, stderr) = RunCompilerCheck("FFI/NamedArgsWithoutStdlib.cvl");
		Assert.Equal(0, exitCode);
		Assert.DoesNotContain("CVL1705", stderr);

		var (ast, context) = AnalyzeProject("FFI/NamedArgsWithoutStdlib.cvl");
		Assert.NotNull(ast);
		Assert.True(context.NativeLibraries.TryGetValue("test", out var lib));
		Assert.Equal("./x.dll", lib!.WinPath);
		Assert.Equal("./lib.so", lib.LinuxPath);
		Assert.Equal("./lib.dylib", lib.MacPath);
	}

	[Fact]
	public void ValidExternBlock_CompileCheck_Succeeds()
	{
		var (ast, context) = AnalyzeProject("FFI/ValidExternBlock.cvl");

		Assert.NotNull(ast);
		Assert.Empty(context.Diagnostics.Diagnostics.Where(d => d.Severity == Cvolo.Core.Diagnostics.DiagnosticSeverity.Error));
	}

	[Fact]
	public void ImportNameOverride_RegistersNativeName()
	{
		var (ast, context) = AnalyzeProject("FFI/ImportNameOverride.cvl");

		Assert.NotNull(ast);
		Assert.Empty(context.Diagnostics.Diagnostics.Where(d => d.Severity == Cvolo.Core.Diagnostics.DiagnosticSeverity.Error));

		var initSymbol = context.Globals.Lookup("Init") as FunctionSymbol;
		Assert.NotNull(initSymbol);
		Assert.True(initSymbol!.IsExtern);
		Assert.Equal("native_init", initSymbol.ImportName);
	}

	[Fact]
	public void LibraryImport_RegistersNativeLibrary()
	{
		var (ast, context) = AnalyzeProject("FFI/ValidExternBlock.cvl");

		Assert.NotNull(ast);
		Assert.True(context.NativeLibraries.ContainsKey("test"));
	}
}
