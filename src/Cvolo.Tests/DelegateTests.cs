using System;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;
using Xunit;

namespace Cvolo.Tests;

public sealed class DelegateTests : CompilerTestBase
{
	private const string Category = "Delegates";

	[Theory]
	[InlineData("DelegatesBasic.cvl")]
	public void Parser_Delegates_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(DelegateTests).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, "TestCases", $"{Category}/{caseName}");

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
	[InlineData("DelegatesBasic", "11\n105\n17\n-8\n6\n")]
	public void Delegates_BasicFlow(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}