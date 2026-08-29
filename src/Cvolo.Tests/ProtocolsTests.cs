using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ProtocolsTests : CompilerTestBase
{
	[Theory]
	[InlineData("ValueDispatch.cvl")]
	[InlineData("ValueDispatchMultiple.cvl")]
	[InlineData("NonConformingFail.cvl")]
	[InlineData("RefDispatch.cvl")]
	[InlineData("RefvarDispatch.cvl")]
	public void Parser_Protocols_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(ProtocolsTests).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, "TestCases", $"Protocols/{caseName}");

		var project = CompilationProject.Load(fullPath);
		var parser = new AntlrSyntaxParser();

		foreach (var file in project.SourceFiles)
		{
			var sourceCode = File.ReadAllText(file);
			var context = new CompilationContext(sourceCode, file);
			var ast = parser.Parse(context);

			Assert.NotNull(ast);
			Assert.False(parser.Diagnostics.HasErrors, $"Expected parser to successfully parse '{caseName}'.");
		}
	}

	[Theory]
	[InlineData("NonConformingFail", "Type 'Foo' does not structurally conform to protocol 'IWidget' for parameter 'w'")]
	public void Protocols_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Protocols/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("ValueDispatch", "Point(3,4)\nValue: 7\nContains4: 1\nContains7: 0")]
	[InlineData("ValueDispatchMultiple", "Point(3,4)\nValue: 7\nCircle(r=5)\nValue: 10")]
	public void Protocols_ValueDispatch(string caseName, string expected)
	{
		var fileName = $"Protocols/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Protocols");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("RefDispatch", "Point(3,4)\nValue: 7")]
	[InlineData("RefvarDispatch", "Area: 48")]
	public void Protocols_RefDispatch(string caseName, string expected)
	{
		var fileName = $"Protocols/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Protocols");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}