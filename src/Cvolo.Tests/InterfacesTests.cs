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
	[InlineData("GenericConformanceDispatch.cvl")]
	[InlineData("GenericInterfaceHierarchy.cvl")]
	[InlineData("GenericInterfaceDispatchOwners.cvl")]
	[InlineData("GenericConformanceWrongSpecializationFail.cvl")]
	[InlineData("GenericInterfaceRequiresFail.cvl")]
	[InlineData("GenericConstraintWrongSpecializationFail.cvl")]
	[InlineData("GenericConformanceMissingMemberFail.cvl")]
	[InlineData("GenericInterfaceDispatchConstraintPass.cvl")]
	[InlineData("GenericInterfaceDispatchConstraintFail.cvl")]
	[InlineData("GenericInterfaceRequiresWrongSignatureFail.cvl")]
	[InlineData("GenericOwnerNonGenericInterface.cvl")]
	[InlineData("GenericInterfaceRequiresIndependentBindings.cvl")]
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
	[InlineData("ConformanceUnknownInterface", "Unknown interface 'IMissingInterface' in conformance declaration. Only an interface can be named here.")]
	[InlineData("ConformanceProtocolTarget", "Extension block cannot implement protocol 'IProtocolOnly': protocols are satisfied structurally, so only an interface can be named in a conformance declaration.")]
	[InlineData("ConformanceNonInterfaceTarget", "'Widget' is not an interface; only an interface can be named in a conformance declaration.")]
	[InlineData("ValueDispatchNotConforming", "Type 'Foo' does not conform to interface 'IWidget' for parameter 'w'")]
	[InlineData("UnresolvedConcreteFail", "Interface parameter 'w' of function 'Draw' cannot be resolved to a concrete conforming type; argument is abstract interface type 'IWidget'")]
	// P7: requires-clause rejection — enforced eagerly when Btn registers IButton
	// without also conforming to IWidget.
	[InlineData("InterfaceRequiresFail", "does not satisfy the requires-clause 'IWidget' of interface 'IButton'")]
	[InlineData("GenericConformanceWrongSpecializationFail", "Type 'Storage<int>' does not conform to interface 'IStorage<long>' for parameter 'storage'")]
	[InlineData("GenericInterfaceRequiresFail", "Type 'Novel' does not satisfy the requires-clause 'ITagged<int>' of interface 'IBook<int>'")]
	[InlineData("GenericConstraintWrongSpecializationFail", "Type 'Novel' does not satisfy required constraint 'IBook<int>' for type parameter 'B'")]
	[InlineData("GenericConformanceMissingMemberFail", "Type 'Storage<int>' does not implement member 'void Set(int)' required by interface 'IStorage<int>'")]
	[InlineData("GenericInterfaceDispatchConstraintFail", "Type 'Other' does not satisfy required constraint 'IValue<int>' for type parameter 'B'")]
	[InlineData("GenericInterfaceRequiresWrongSignatureFail", "Type 'Novel' does not satisfy the requires-clause 'ITagged<int>' of interface 'IBook<int>'")]
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

	/// <summary>
	/// Executes the fixed-arity generic-interface acceptance programs: closed conformance,
	/// substituted inherited members and combined scalar/owner dispatch cache identity.
	/// </summary>
	[Theory]
	[InlineData("GenericConformanceDispatch", "42\n1")]
	[InlineData("GenericInterfaceHierarchy", "42\n142")]
	[InlineData("GenericInterfaceDispatchOwners", "7\n107\n7")]
	[InlineData("GenericInterfaceDispatchConstraintPass", "42")]
	[InlineData("GenericOwnerNonGenericInterface", "42")]
	[InlineData("GenericInterfaceRequiresIndependentBindings", "42")]
	public void Interfaces_GenericContracts(string caseName, string expected)
	{
		var fileName = $"Interfaces/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Interfaces");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	/// <summary>
	/// Sixty-four independent dispatch specializations and one repeated request must all
	/// bind and produce the exact expected owner values at both optimization levels.
	/// </summary>
	[Fact]
	public void GenericDispatchStress_SixtyFourIndependentSpecializations()
	{
		var expected = string.Join("\n", Enumerable.Range(0, 64).Select(i => i.ToString()).Append("0"));
		foreach (var optimization in new[] { "-O0", "-O3" })
		{
			var fileName = "Interfaces/GenericDispatchStress.cvl";
			var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
			AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

			var (runCode, runStdout) = ExecuteBinary("GenericDispatchStress", "Interfaces");
			Assert.Equal(0, runCode);
			Assert.Equal(expected, runStdout.Replace("\r\n", "\n").Trim());
			Assert.Equal(expected, runStdout.Replace("\r\n", "\n").Trim());
		}

		var (_, context) = AnalyzeProject("Interfaces/GenericDispatchStress.cvl");
		var dispatchKeys = context.MonomorphizedFunctions.Keys.Where(k => k.StartsWith("Read<")).ToList();
		Assert.Equal(64, dispatchKeys.Count);
		Assert.All(dispatchKeys, key => Assert.Equal(Cvolo.Analysis.MonomorphizationState.Ready, context.MonomorphizationStates[key]));
	}
}
