using System;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;
using Xunit;

namespace Cvolo.Tests;

public sealed class IntPrimitivesTests : CompilerTestBase
{
	private const string Category = "IntPrimitives";

	[Theory]
	[InlineData("IntPrimitives.cvl")]
	public void Parser_IntPrimitives_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(IntPrimitivesTests).Assembly.Location)!;
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
	[InlineData("IntPrimitives", "200\n-5\n-1234\n5678\n4000000000\n9000000000\n18000000000\n46\n200\n820130816\n130\n10240\n1\n8\n200\n")]
	public void IntPrimitives_ExactWidths(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}