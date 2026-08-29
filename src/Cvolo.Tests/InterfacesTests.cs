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
	[InlineData("ValueDispatch.cvl")]
	[InlineData("ValueDispatchMultiple.cvl")]
	[InlineData("ValueDispatchNotConforming.cvl")]
	[InlineData("RefDispatch.cvl")]
	[InlineData("RefvarDispatch.cvl")]
	[InlineData("Integration.cvl")]
	[InlineData("UnresolvedConcreteFail.cvl")]
	// P7: colon inheritance, protocol-as-base, requires-clause (ok+fail) — must PARSE.
	[InlineData("InterfaceInheritance.cvl")]
	[InlineData("InterfaceProtocolBase.cvl")]
	[InlineData("InterfaceRequires.cvl")]
	[InlineData("InterfaceRequiresFail.cvl")]
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
	[InlineData("ValueDispatchNotConforming", "Type 'Foo' does not conform to interface 'IWidget' for parameter 'w'")]
	[InlineData("UnresolvedConcreteFail", "Interface parameter 'w' of function 'Draw' cannot be resolved to a concrete conforming type; argument is abstract interface type 'IWidget'")]
	// P7: requires-clause rejection — enforced eagerly when Btn registers IButton
	// without also conforming to IWidget.
	[InlineData("InterfaceRequiresFail", "does not satisfy the requires-clause 'IWidget' of interface 'IButton'")]
	public void Interfaces_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Interfaces/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("RefDispatch", "Point(3,4)\nValue: 7")]
	[InlineData("RefvarDispatch", "Area: 48")]
	[InlineData("Integration", "Player(h=10)\nId: 20\nEnemy(h=4)\nId: 12\nPlayer(h=10)")]
	public void Interfaces_RefDispatch(string caseName, string expected)
	{
		var fileName = $"Interfaces/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Interfaces");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	// P7 hierarchy: InterfaceInheritance tests interface->interface inheritance;
	// InterfaceProtocolBase tests interface->protocol inheritance (structural members
	// through a nominally-dispatchable interface); InterfaceRequires tests a satisfied
	// requires-clause end to end.
	[Theory]
	[InlineData("InterfaceInheritance", "Button Print\nValue: 5\nbutton clicked")]
	[InlineData("InterfaceProtocolBase", "key=42\nentry described")]
	[InlineData("InterfaceRequires", "labeled")]
	public void Interfaces_Hierarchy(string caseName, string expected)
	{
		var fileName = $"Interfaces/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Interfaces");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}
