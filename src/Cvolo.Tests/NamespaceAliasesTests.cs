using System.Text.Json;
using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Packaging;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// Feature: Namespace Alias Using Directives (spec 0.0.21).
/// <c>using Alias = Namespace.Path;</c> creates a lexical, compile-time namespace-prefix
/// binding: it expands the namespace prefix only, emits no runtime symbol, is never
/// serialized into package metadata, and a runtime value with the same spelling wins.
/// </summary>
public sealed class NamespaceAliasesTests : CompilerTestBase
{
	private const string Category = "Namespaces";

	private static CompilationUnitSyntax Parse(string source, string file, Binder binder)
	{
		var context = new CompilationContext(source, file);
		var ast = new AntlrSyntaxParser().Parse(context);
		Assert.NotNull(ast);
		binder.Context.FileContexts[ast!] = context;
		return ast!;
	}

	[Theory]
	[InlineData(
		"NamespaceAliasBasic",
		"PI: 3.141590\n" +
		"Counter: 5\n" +
		"Sum: 3.000000\n" +
		"Sqrt: 3.000000\n" +
		"ScopedPi: 3.141590\n" +
		"ScopedSum: 5.000000\n" +
		"Vec: 3.000000, 4.000000\n" +
		"Magnitude: 25.000000\n" +
		"Half: 1.570795\n" +
		"Callback: 42")]
	[InlineData("NamespaceAliasValuePrecedence", "Value: 7")]
	[InlineData("NamespaceAliasOpenGl", "Viewport: 1, 2, 3, 4")]
	public void NamespaceAlias_Execution_Success(string caseName, string expected)
	{
		var (compileCode, stdout, stderr) = RunCompiler($"{Category}/{caseName}");
		AssertCompilationSucceeded(compileCode, stdout, stderr, $"{Category}/{caseName}");

		var (runCode, runStdout) = ExecuteBinary(caseName, $"{Category}/{caseName}");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("NamespaceAliasUnknownTargetFail.cvl", "CVL4178", "namespace alias target 'Not.Here' does not exist")]
	[InlineData("NamespaceAliasToTypeFail", "CVL4178", "namespace alias target 'Aliasing.Point' does not exist")]
	[InlineData("NamespaceAliasExposeFail.cvl", "CVL4173", "unexpected token '='")]
	[InlineData("NamespaceAliasNoImport", "CVL4128", "Unknown type 'Vec2'")]
	[InlineData("NamespaceAliasDuplicate", "CVL4179", "Duplicate namespace alias 'Dup' in the same scope.")]
	[InlineData("NamespaceAliasFileScopeIsolated", "CVL4114", "Undefined variable 'Native'")]
	public void NamespaceAlias_Rejections(string caseName, string expectedCode, string expectedError)
	{
		var (exitCode, stdout, stderr) = RunCompiler($"{Category}/{caseName}");

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedCode, stderr);
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void NamespaceAlias_HasNoRuntimeOrSymbolTableArtifact()
	{
		var (asts, context) = AnalyzeProject($"{Category}/NamespaceAliasBasic");
		Assert.NotNull(asts);

		foreach (var alias in new[] { "Const", "Db", "Native" })
		{
			Assert.False(context.GlobalsByQualifiedName.ContainsKey(alias), $"GlobalsByQualifiedName leaked '{alias}'.");
			Assert.False(context.GlobalsByShortName.ContainsKey(alias), $"GlobalsByShortName leaked '{alias}'.");
			Assert.False(context.StructTypes.ContainsKey(alias), $"StructTypes leaked '{alias}'.");
			Assert.False(context.UnionTypes.ContainsKey(alias), $"UnionTypes leaked '{alias}'.");
			Assert.False(context.EnumTypes.ContainsKey(alias), $"EnumTypes leaked '{alias}'.");
			Assert.False(context.DelegateTypes.ContainsKey(alias), $"DelegateTypes leaked '{alias}'.");
			Assert.False(context.DeclaredNamespaces.Contains(alias), $"DeclaredNamespaces leaked '{alias}'.");
		}

		// The alias lives only in the compile-time alias table of the file that declared it.
		var mainUnit = asts!.First(unit => System.IO.Path.GetFileName(unit.Context!.FilePath) == "Main.cvl");
		var aliases = context.GetNamespaceAliases(mainUnit);
		Assert.Equal("MathLib.Constants", aliases["Const"].NamespaceName);
		Assert.Equal("MathLib.Native", aliases["Native"].NamespaceName);
		Assert.Equal("MathLib.Constants", aliases["Const"].NamespaceName);
		Assert.Equal("MathLib.Native", aliases["Native"].NamespaceName);
	}

	[Fact]
	public void NamespaceAlias_IsNotSerializedIntoPackageApiMetadata()
	{
		var package = Parse(
			"using System;\n" +
			"using Native = MathLib.Constants;\n" +
			"namespace MathLib.Constants;\n" +
			"\n" +
			"public struct Vec2 { public double X; public double Y; }\n" +
			"public global double MATH_PI = 3.14159;\n",
			"pkg.cvl",
			new Binder());

		var metadata = PackageApiMetadata.FromCompilationUnits([package]);
		var apiUnit = Assert.Single(metadata.Units);

		Assert.Contains("System", apiUnit.FileUsings);
		Assert.DoesNotContain("Native", apiUnit.FileUsings);
		Assert.Empty(apiUnit.NamespaceUsings);

		// An alias is not part of the package API, so it never survives a metadata round-trip.
		var reread = JsonSerializer.Deserialize<PackageApiMetadata>(metadata.Serialize());
		Assert.NotNull(reread);
		var rereadUnit = Assert.Single(reread!.Units);
		Assert.Contains("System", rereadUnit.FileUsings);
		Assert.DoesNotContain("Native", rereadUnit.FileUsings);
	}

	[Fact]
	public void NamespaceAlias_WorksOverPackageRehydratedNamespaces()
	{
		var binder = new Binder();

		var package = Parse(
			"namespace OpenGL.Native;\n" +
			"public unsafe \"C\" delegate void ViewportProc(int x, int y, int width, int height);\n" +
			"public global var ViewportProc Viewport;\n",
			"package.cvl",
			binder);

		var metadata = PackageApiMetadata.FromCompilationUnits([package]);
		var reread = JsonSerializer.Deserialize<PackageApiMetadata>(metadata.Serialize());
		Assert.NotNull(reread);

		var units = new List<CompilationUnitSyntax>();
		foreach (var rehydrated in reread!.CreateCompilationUnits("OpenGL", "1.0.0"))
		{
			binder.Context.FileContexts[rehydrated] = rehydrated.Context;
			units.Add(rehydrated);
		}

		// The consumer re-declares its own alias over the package-rehydrated namespace.
		units.Add(Parse(
			"using Native = OpenGL.Native;\n" +
			"\n" +
			"unsafe int Main() {\n" +
			"    Native.Viewport = &Record;\n" +
			"    Native.Viewport(1, 2, 3, 4);\n" +
			"    return 0;\n" +
			"}\n" +
			"\n" +
			"unsafe \"C\" void Record(int x, int y, int width, int height) { return; }\n",
			"consumer.cvl",
			binder));

		binder.Bind(units);

		Assert.False(
			binder.Diagnostics.HasErrors,
			string.Join("\n", binder.Diagnostics.Diagnostics));
	}
}