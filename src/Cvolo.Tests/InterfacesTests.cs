using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class InterfacesTests : CompilerTestBase
{
	[Theory]
	[InlineData("Conformance.cvl")]
	[InlineData("ConformanceMissingMember.cvl")]
	[InlineData("ConformanceUnknownInterface.cvl")]
	public void Parser_Interfaces_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(InterfacesTests).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, "TestCases", $"Interfaces/{caseName}");

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
	[InlineData("ConformanceMissingMember", "Type 'Point' does not implement member 'int Value()' required by interface 'IWidget'.")]
	[InlineData("ConformanceUnknownInterface", "Unknown interface 'IMissingInterface' in conformance declaration.")]
	public void Interfaces_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Interfaces/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
