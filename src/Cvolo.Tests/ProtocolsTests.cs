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
	[InlineData("SelfDispatch.cvl")]
	[InlineData("SelfConformingFail.cvl")]
	[InlineData("GenericDispatch.cvl")]
	[InlineData("WidthLockFail.cvl")]
	// P6: protocol default implementations (extension block on the protocol).
	[InlineData("DefaultDispatch.cvl")]
	[InlineData("DefaultOverride.cvl")]
	// P7: colon inheritance, inherited defaults, collision rule, requires-clause
	// (ok+fail) and multi-file ambiguity — all must PARSE (or the compiler can't reach its checks).
	[InlineData("ProtocolInheritance.cvl")]
	[InlineData("ProtocolInheritedDefault.cvl")]
	[InlineData("ProtocolCollisionFail.cvl")]
	[InlineData("ProtocolRequires.cvl")]
	[InlineData("ProtocolRequiresFail.cvl")]
	[InlineData("ProtocolAmbiguityFail")]
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
	// P7: collision — same method twice on one type is rejected.
	[InlineData("ProtocolCollisionFail", "Duplicate symbol 'DoWork' on type 'Thing' in extension blocks.")]
	// P7: requires-clause — a type without the required capability can't satisfy the protocol.
	[InlineData("ProtocolRequiresFail", "does not satisfy the requires-clause 'IRecordId' of protocol 'ISorter'")]
	// P7: ambiguity — two matching extension methods make dispatch non-deterministic.
	[InlineData("ProtocolAmbiguityFail", "Ambiguous implementation of 'DoWork' for protocol 'IWorker' on type 'Point'")]
	public void Protocols_Rejections(string caseName, string expectedError)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(ProtocolsTests).Assembly.Location)!;
		var fileName = File.Exists(Path.Combine(assemblyDir, "TestCases", $"Protocols/{caseName}.cvl"))
			? $"Protocols/{caseName}.cvl"
			: $"Protocols/{caseName}";
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

	[Theory]
	[InlineData("SelfConformingFail", "does not structurally conform to protocol 'IEquatable' for parameter 'e'")]
	[InlineData("WidthLockFail", "Type 'IntBag' does not structurally conform to protocol 'IContainer<int>' for parameter 'c'")]
	public void Protocols_RejectionsGeneric(string caseName, string expectedError)
	{
		var fileName = $"Protocols/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("GenericDispatch", "stored=13\nfinal=13\npoint=ok")]
	public void Protocols_GenericDispatch(string caseName, string expected)
	{
		var fileName = $"Protocols/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Protocols");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("SelfDispatch", "cmp1=1\ncmp2=0\nclone=(7,8)")]
	public void Protocols_SelfDispatch(string caseName, string expected)
	{
		var fileName = $"Protocols/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Protocols");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	// P6 defaults: DefaultDispatch exercises the inherited body; DefaultOverride
	// proves a conformer's own method shadows the default. Both dispatch through a
	// protocol-typed parameter, so codegen must land on the right LLVM function.
	[Theory]
	[InlineData("DefaultDispatch", "Document(pages=12)\nDefault Info Output")]
	[InlineData("DefaultOverride", "Document(pages=12)\nDocument Info Override")]
	public void Protocols_DefaultDispatch(string caseName, string expected)
	{
		var fileName = $"Protocols/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Protocols");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	// P7 hierarchy: ProtocolInheritance tests multi-base aggregation; 
	// ProtocolInheritedDefault tests defaults surviving protocol inheritance; 
	// ProtocolRequires tests a satisfied requires-clause end to end.
	[Theory]
	[InlineData("ProtocolInheritance", "File read at 3\nFile write\nFile flush")]
	[InlineData("ProtocolInheritedDefault", "device go 7\nBase Info default\nBase Extra default")]
	[InlineData("ProtocolRequires", "sorted")]
	public void Protocols_Hierarchy(string caseName, string expected)
	{
		var fileName = $"Protocols/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Protocols");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}