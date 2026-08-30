using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class EnumsTests : CompilerTestBase
{
	private const string Category = "Enums";

	[Theory]
	[InlineData("ScopedAccessBasic.cvl")]
	public void Parser_Enums_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(EnumsTests).Assembly.Location)!;
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

	[Fact]
	public void AnalyzeProject_Enum_ScopedAccess_Resolves_Symbols()
	{
		var (asts, context) = AnalyzeProject($"{Category}/ScopedAccessBasic.cvl");

		var level = Assert.IsType<EnumTypeSymbol>(context.ResolveType("Level"));
		Assert.Equal(TypeSymbol.Byte, level.StorageType);
		Assert.Equal(3, level.Variants.Count);
		Assert.Equal(1, level.FindVariant("Low")!.Value);
		Assert.Equal(2, level.FindVariant("Mid")!.Value);
		Assert.Equal(3, level.FindVariant("High")!.Value);
		Assert.False(level.IsFlags);
		Assert.False(level.IsNonExhaustive);

		var color = Assert.IsType<EnumTypeSymbol>(context.ResolveType("Color"));
		Assert.Equal(TypeSymbol.Int, color.StorageType);
		Assert.Equal(3, color.Variants.Count);
		Assert.Equal(0, color.FindVariant("Red")!.Value);
		Assert.Equal(5, color.FindVariant("Green")!.Value);
		Assert.Equal(6, color.FindVariant("Blue")!.Value);
	}

	[Theory]
	[InlineData("ScopedAccessBasic", "3\n1\n2\n6\n0\n5\n1\n4\n3\n5\n")]
	public void Enum_ScopedAccess_Prints_ScalarValues(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("SafeCast", "None\nSome: 1\nSome: 2\nSome: 3\nNone\nTemp: -2\nTemp: -1\nTemp: 0\nNoTemp\nNoTemp\nNoTemp\nNoTemp\nNoTemp\nActive: 1\n")]
	public void Enum_SafeCast_Produces_Option(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("UnsafeDirectCast", "raw: 7\nraw2: 5\nraw3: 2\n")]
	public void Enum_UnsafeDirectCast_Produces_RawValue(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("ExtensionDispatch", "Status value: 1\nStatus value: 2\n")]
	[InlineData("ExtensionInnerVariant", "2\n0\n1\n1\n")]
	[InlineData("ExtensionRefReceiver", "Inspect: 1\nInspect: 2\n")]
	public void Enum_ExtensionMethods_Dispatch(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("EnumExtensionUnknownVariantFail.cvl", "Undefined variable 'Bogus'")]
	[InlineData("EnumExtensionNoMethodFail.cvl", "No overload of function 's.Nope' matches argument types ()")]
	[InlineData("EnumUnqualifiedOutsideExtensionFail.cvl", "Undefined variable 'Active'")]
	public void Enum_Extension_Rejections(string caseName, string expectedError)
	{
		var (exitCode, stdout, stderr) = RunCompiler($"{Category}/{caseName}");
		Assert.NotEqual(0, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("EnumEmptyFail.cvl", "missing Identifier at '}'")]
	[InlineData("EnumDuplicateVariantFail.cvl", "Duplicate variant 'A' in enum 'Dupe'")]
	[InlineData("EnumDuplicateTypeFail.cvl", "Duplicate type definition 'Collide'")]
	[InlineData("EnumBadStorageFail.cvl", "Invalid underlying storage type 'float' for enum 'Bad'")]
	[InlineData("EnumUnknownVariantFail.cvl", "Enum 'Level' does not contain variant 'Missing'")]
	[InlineData("EnumImplicitIntFail.cvl", "Cannot initialize variable of type 'Level' with value of type 'int'")]
	[InlineData("EnumImplicitBinaryFail.cvl", "Implicit conversion between enum 'Status' and 'int' is forbidden; use an explicit cast.")]
	[InlineData("EnumImplicitAssignFail.cvl", "Implicit conversion between enum 'Status' and 'int' is forbidden; use an explicit cast.")]
	[InlineData("EnumImplicitArgFail.cvl", "No overload of function 'takeInt' matches argument types (Status)")]
	[InlineData("EnumSafeCastBindFail.cvl", "Cannot initialize variable of type 'Status' with value of type 'System.Option<Status>'")]
	[InlineData("EnumReturnMismatchFail.cvl", "Function 'getIt' expects return type 'int' but found 'Status'")]
	public void Enum_Rejections(string caseName, string expectedError)
	{
		var (exitCode, stdout, stderr) = RunCompiler($"{Category}/{caseName}");
		Assert.NotEqual(0, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
