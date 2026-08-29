using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class EmbedTests : CompilerTestBase
{
	[Theory]
	[InlineData("EmbedBasic.cvl")]
	[InlineData("EmbedProtoConformance.cvl")]
	[InlineData("EmbedChain.cvl")]
	[InlineData("EmbedOverride.cvl")]
	[InlineData("EmbedCollisionFail.cvl")]
	[InlineData("EmbedGenericFail.cvl")]
	[InlineData("EmbedInterfaceNonTransitiveFail.cvl")]
	public void Parser_Embed_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(EmbedTests).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, "TestCases", $"Embed/{caseName}");

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
	// Embed collision — a field already provided by the embedded type may not be redeclared.
	[InlineData("EmbedCollisionFail", "Field 'b' of struct 'W' conflicts with embedded field from 'B'.")]
	// Generic templates cannot be embedded (instances build fields from source text only).
	[InlineData("EmbedGenericFail", "Cannot use embed in generic struct template 'W'")]
	// Nominal interface conformance does NOT propagate through embed.
	[InlineData("EmbedInterfaceNonTransitiveFail", "Type 'Warrior' does not conform to interface 'IRecord' for parameter 'r'")]
	public void Embed_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Embed/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	// EmbedBasic — flattened base fields + promoted embedded method hooked onto the outer struct.
	[InlineData("EmbedBasic", "hp=70 sword=45")]
	// EmbedChain — fields and promotions compose across multi-level embed chains.
	[InlineData("EmbedChain", "a=1 b=2 c=3")]
	// EmbedOverride — the outer's own method shadows the promoted embedded copy.
	[InlineData("EmbedOverride", "warrior hp=100+45")]
	// EmbedProtoConformance — promoted methods alone satisfy a protocol structurally.
	[InlineData("EmbedProtoConformance", "hp=95")]
	public void Embed_Composition(string caseName, string expected)
	{
		var fileName = $"Embed/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Embed");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}