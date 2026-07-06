using Cvolo.Core.Diagnostics;
using Cvolo.Syntax;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class GenericsTests : CompilerTestBase
{
	[Theory]
	[InlineData("GenericStruct.cvl")]
	[InlineData("GenericFunction.cvl")]
	[InlineData("GenericComplex.cvl")]
	public void Parser_Generics_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(GenericsTests).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, "TestCases", $"Generics/{caseName}");

		var project = CompilationProject.Load(fullPath);
		var parser = new SyntaxParser();

		foreach (var file in project.SourceFiles)
		{
			var sourceCode = File.ReadAllText(file);
			var context = new CompilationContext(sourceCode, file);
			var ast = parser.Parse(context);

			// Assert that the parser successfully processes all generic definitions and usages
			Assert.NotNull(ast);
			Assert.False(parser.Diagnostics.HasErrors, $"Expected parser to successfully parse '{caseName}'.");
		}
	}

	[Theory]
	[InlineData("GenericStruct", "First: 42, Second: 3.140000")]
	[InlineData("GenericFunction", "X: 200, Y: 100")]
	[InlineData("GenericComplex", "X: 20, Y: 10")]
	public void Generics_Execution(string caseName, string expected)
	{
		var fileName = $"Generics/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Generics");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}
}
