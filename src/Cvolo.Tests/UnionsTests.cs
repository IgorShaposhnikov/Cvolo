using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnionsTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnionDecl.cvl")]
	public void Parser_Unions_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(UnionsTests).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, "TestCases", $"Unions/{caseName}");

		var project = CompilationProject.Load(fullPath);
		var parser = new AntlrSyntaxParser();

		foreach (var file in project.SourceFiles)
		{
			var sourceCode = File.ReadAllText(file);
			var context = new CompilationContext(sourceCode, file);
			var ast = parser.Parse(context);

			Assert.NotNull(ast);
			Assert.False(parser.Diagnostics.HasErrors, $"Expected parser to successfully parse union '{caseName}'.");
		}
	}
}
