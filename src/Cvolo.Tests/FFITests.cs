using Cvolo.Analysis.Symbols;
using Cvolo.Core.AST.Base;
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

	[Theory]
	[InlineData("FFI/ExternBlockExplicitInternal.cvl", "Test.Native.Init", Visibility.Internal)]
	[InlineData("FFI/ExternBlockExplicitPrivate.cvl", "Test.Native.Init", Visibility.Private)]
	[InlineData("FFI/ExternBlockDefaultInternal.cvl", "Test.Native.Init", Visibility.Internal)]
	public void ExternBlockFunction_Visibility_Is_Bound(string caseFile, string symbolName, Visibility expected)
	{
		var (ast, context) = AnalyzeProject(caseFile);

		Assert.NotNull(ast);
		Assert.Empty(context.Diagnostics.Diagnostics.Where(d => d.Severity == Cvolo.Core.Diagnostics.DiagnosticSeverity.Error));

		var symbol = context.Globals.Lookup(symbolName) as FunctionSymbol;
		Assert.NotNull(symbol);
		Assert.Equal(expected, symbol!.Visibility);
		Assert.True(symbol.IsExtern);
	}

	[Fact]
	public void PublicExternBlockFunction_Is_Rejected()
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck("FFI/PublicExternBlockFunction.cvl");

		Assert.Equal(1, exitCode);
		Assert.Contains("Compile Error CVL1033", stderr);
	}

	[Fact]
	public void InternalExternBlockFunction_Is_Visible_CrossFile_In_Module()
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck("FFI/InternalExternCrossFile");

		Assert.Equal(0, exitCode);
		Assert.DoesNotContain("No overload", stderr);
	}

	[Fact]
	public void ImportName_With_Internal_Uses_Source_Name_For_Lookup()
	{
		var (ast, context) = AnalyzeProject("FFI/ImportNameInternal.cvl");

		Assert.NotNull(ast);
		Assert.Empty(context.Diagnostics.Diagnostics.Where(d => d.Severity == Cvolo.Core.Diagnostics.DiagnosticSeverity.Error));

		var symbol = context.Globals.Lookup("Test.Native.Init") as FunctionSymbol;
		Assert.NotNull(symbol);
		Assert.Equal("native_init", symbol!.ImportName);
		Assert.Null(context.Globals.Lookup("Test.Native.native_init"));
	}
}
