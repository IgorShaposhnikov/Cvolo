using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class CvoloSourcePrinterTests : CompilerTestBase
{
	[Fact]
	public void Print_EveryTestNode_ContainsNoRawClrTypeNames()
	{
		var assemblyDir = Path.GetDirectoryName(typeof(CvoloSourcePrinterTests).Assembly.Location)!;
		var testCasesRoot = Path.Combine(assemblyDir, TestCasesDirectory);
		var failures = new List<string>();

		foreach (var file in Directory.EnumerateFiles(testCasesRoot, "*.cvl", SearchOption.AllDirectories))
		{
			var parser = new AntlrSyntaxParser();
			var ast = parser.Parse(new CompilationContext(File.ReadAllText(file), file));
			if (ast is null)
			{
				continue;
			}

			foreach (var node in EnumerateNodes(ast))
			{
				var printed = CvoloSourcePrinter.Print(node);
				if (printed.Contains("Cvolo.Core.AST", StringComparison.Ordinal))
				{
					failures.Add($"{Path.GetFileName(file)}: {node.GetType().Name} printed as '{printed}'");
				}
			}
		}

		Assert.True(
			failures.Count == 0,
			"Raw CLR type names leaked from CvoloSourcePrinter for the following nodes:\n" + string.Join("\n", failures.Take(100)));
	}

	[Fact]
	public void Print_CoreDeclarationKinds_RendersReadableSource()
	{
		const string source = """
			namespace Test;
			struct Point { int x; int y; }
			extern int strlen(string s);
			protocol Drawable { int draw(); }
			interface Runner { int run(Point p); }
			int main() {
				val point = Point(1, 2);
				if (point.x > 0) { return point.y; } else { return 0; }
			}
			""";

		var parser = new AntlrSyntaxParser();
		var ast = parser.Parse(new CompilationContext(source, "printer.cvl"));

		Assert.NotNull(ast);
		Assert.False(parser.Diagnostics.HasErrors, $"Parsing of printer snippet failed: {parser.Diagnostics}");
		var printed = CvoloSourcePrinter.Print(ast);

		Assert.DoesNotContain("Cvolo.Core.AST", printed);
		Assert.Contains("struct Point", printed);
		Assert.Contains("extern int strlen(string s);", printed);
		Assert.Contains("protocol Drawable", printed);
		Assert.Contains("interface Runner", printed);
		Assert.Contains("if (point.x > 0)", printed);
		Assert.Contains("val point = Point(1, 2);", printed);
	}

	[Fact]
	public void Kitchensink_Compiles_And_Runs()
	{
		var fileName = "Printer/KitchenSink.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("KitchenSink", "Printer");
		Assert.Equal(0, runCode);
		Assert.Equal("308\ndone 5 1 total\nPoint\nfinal 359 3 7\n".Replace("\r\n", "\n"), runStdout.Replace("\r\n", "\n"));
	}

	[Fact]
	public void Kitchensink_LoweredOutput_IsReadable_AndContainsNoRawTypeNames()
	{
		var fileName = "Printer/KitchenSink.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--emit-lowered");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var lowered = stdout.Replace("\r\n", "\n");
		Assert.DoesNotContain("Cvolo.Core.AST", lowered);
		Assert.DoesNotContain("foreach", lowered);

		var expectedMarkers = new[]
		{
			"struct Point {",
			"union Opt<T> {",
			"enum Status : byte",
			"protocol IPrintable {",
			"interface IRunnable {",
			"extension Point {",
			"extension Runner : IRunnable",
			"alias ID = int;",
			"global var int Calls = 0;",
			"extern void printf(string format, ...);",
			"Result<int, ErrorCodes> Divide",
			"while (q < 3) {",
			"for (val int i = 0",
			"val int __fe_len0 = 5;",
			"for (var int __fe_i0 = 0; __fe_i0 < __fe_len0; __fe_i0 = __fe_i0 + 1) {",
			"val item = nums[__fe_i0];",
			"val int __fe_len1 = 5;",
			"for (var int __fe_i1 = 0; __fe_i1 < __fe_len1; __fe_i1 = __fe_i1 + 1) {",
			"val refvar item = ref nums[__fe_i1];",
			"switch (s) {",
			"case Active:",
			"var bool __try_failed_0 = false;",
			"if (__try_failed_0) {",
			"val __try_res_4 = Divide(10, 2);",
			"var int[] buffer = heap int[3];",
			"if (opt is Some seen) {",
			"if (ref opt is None) {",
			"nameof(total)",
			"typeof(Point)",
			"var Runner rn = Runner { Speed: 5 };"
		};

		foreach (var marker in expectedMarkers)
		{
			Assert.True(lowered.Contains(marker, StringComparison.Ordinal), $"Expected lowered output to contain '{marker}'.");
		}
	}

	private static IEnumerable<SyntaxNode> EnumerateNodes(SyntaxNode root)
	{
		yield return root;
		foreach (var child in root.GetChildren())
		{
			foreach (var node in EnumerateNodes(child))
			{
				yield return node;
			}
		}
	}
}
