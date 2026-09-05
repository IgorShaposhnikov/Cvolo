using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class GenericsTests : CompilerTestBase
{
	[Theory]
	[InlineData("GenericStruct.cvl")]
	[InlineData("GenericFunction.cvl")]
	[InlineData("GenericComplex.cvl")]
	[InlineData("GenericExtension.cvl")]
	[InlineData("GenericConstructor.cvl")]
	[InlineData("GenericExtensionComplex.cvl")]
	[InlineData("GenericConstructorComplex.cvl")]
	[InlineData("DefaultGenericParams.cvl")]
	[InlineData("DefaultOperator.cvl")]
	[InlineData("DefaultGenericParamsExtension.cvl")]
	public void Parser_Generics_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(GenericsTests).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, "TestCases", $"Generics/{caseName}");

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
	[InlineData("GenericStruct", "First: 42, Second: 3.140000")]
	[InlineData("GenericFunction", "X: 200, Y: 100")]
	[InlineData("GenericComplex", "X: 20, Y: 10")]
	[InlineData("GenericExtension", "First: 7, Second: 2.5")]
	[InlineData("GenericConstructor", "X: 30, Y: 40")]
	[InlineData("GenericExtensionComplex", "[Pair] First is active, Second is active\np1: 42, 3.140000\n[Pair] First is active, Second is active\np2: C, 1")]
	[InlineData("GenericConstructorComplex", "Id: 101\nValue: 99.9")]
	[InlineData("DefaultGenericParamsExtension", "b0: 10\nb1: 20\nb2: 30\nb3: 40")]
	public void Generics_Execution(string caseName, string expected)
	{
		var fileName = $"Generics/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Generics");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("GenericConstructorDefensiveFail", "does not initialize field 'Weight'")]
	[InlineData("DefaultGenericParamsBadDefault", "Default value for generic parameter 'A' must be a Trivial Copy Type")]
	[InlineData("DefaultGenericParamsMissingDefault", "Generic parameter 'A' does not have a default value and must be specified")]
	public void Generics_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Generics/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void AnalyzeProject_DefaultGenericParams_FillsInDefaultType()
	{
		var (asts, context) = AnalyzeProject("Generics/DefaultGenericParams.cvl");

		var listType = context.ResolveType("List<int>");
		Assert.NotNull(listType);

		var listStruct = Assert.IsType<StructTypeSymbol>(listType);
		Assert.Contains("MallocAllocator", listStruct.Name);

		var allocatorField = listStruct.Fields.FirstOrDefault(f => f.Name == "Allocator");
		Assert.NotNull(allocatorField);
		Assert.Equal("MallocAllocator", allocatorField!.Type.Name);
	}

	[Fact]
	public void AnalyzeProject_DefaultOperator_ResolvesTrivialCopyType()
	{
		var (asts, context) = AnalyzeProject("Generics/DefaultOperator.cvl");

		var diagnostics = context.Diagnostics.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
		var defaultErrors = diagnostics.Where(d => d.Message.Contains("must be a Trivial Copy Type") && d.Message.Contains("default")).ToList();
		Assert.Empty(defaultErrors);
	}

	[Fact]
	public void AnalyzeProject_DefaultGenericParams_BadDefault_ReportsCVL1040()
	{
		var (asts, context) = AnalyzeProject("Generics/DefaultGenericParamsBadDefault.cvl");

		var diagnostics = context.Diagnostics.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
		Assert.Contains(diagnostics, d => d.Id == DiagnosticIds.DefaultMustBeTrivialCopy);
	}
}
