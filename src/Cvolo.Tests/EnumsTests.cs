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
	[InlineData("EnumSwitchCover", "None\nActive\nPending\nInactive\nNone\n")]
	[InlineData("EnumSwitchExtension", "10\n20\n30\n")]
	[InlineData("EnumSwitchDefault", "Other\n")]
	public void Enum_EnumSwitch_ExhaustiveDispatch(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("EnumSwitchMissingVariantFail.cvl", "Switch statement is not exhaustive. Missing case for variant 'Inactive'.")]
	[InlineData("EnumSwitchUnknownVariantFail.cvl", "Enum 'Status' does not contain variant 'Bogus'")]
	[InlineData("EnumSwitchVarPatternFail.cvl", "Enum variants cannot carry a promoted variable.")]
	[InlineData("EnumSwitchIntTargetFail.cvl", "Switch statement target must be a union type.")]
	public void Enum_EnumSwitch_Rejections(string caseName, string expectedError)
	{
		var (exitCode, stdout, stderr) = RunCompiler($"{Category}/{caseName}");
		Assert.NotEqual(0, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("FlagsAutoBitmask", "0\n1\n2\n3\n4\n")]
	[InlineData("FlagsOperators", "3\n4\n1\n2\nhas write\n")]
	[InlineData("EnumFlagsSwitchRelaxed", "read\n")]
	public void Enum_Flags_Dispatch(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("EnumFlagsSignedStorageFail.cvl", "Invalid underlying storage type 'int' for [Flags] enum 'Bad': [Flags] enums require unsigned storage (uint, ushort, byte, ulong, or char).")]
	[InlineData("EnumFlagsZeroNameFail.cvl", "Variant 'Something' in [Flags] enum 'Bad' has value 0 and must be named None, Empty, Unset, or Zero.")]
	[InlineData("EnumFlagsCollisionFail.cvl", "Variant 'ReadAlso' in [Flags] enum 'Bad' collides with an existing value '2'.")]
	[InlineData("EnumFlagsTildeNonFlagsFail.cvl", "Operator '~' cannot be applied to non-[Flags] enum 'Plain'.")]
	[InlineData("EnumFlagsHasFlagArgFail.cvl", "HasFlag expects exactly one argument of the same [Flags] enum type 'Permissions'.")]
	public void Enum_Flags_Rejections(string caseName, string expectedError)
	{
		var (exitCode, stdout, stderr) = RunCompiler($"{Category}/{caseName}");
		Assert.NotEqual(0, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("NameValues", "Mid\nHappy\n1\n2\n4\n")]
	[InlineData("MinMaxCount", "3\n1\n4\n40 10 7\n")]
	[InlineData("FlagsValues", "1\n2\n2\n")]
	public void Enum_EnumMetaprogramming_Dispatch(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("EnumMetaSizeNegativeMinFail.cvl", "Cannot resolve type 'int[Mood.Min+1]'.")]
	[InlineData("EnumMetaSizeUnknownFail.cvl", "Cannot resolve type 'int[wibble]'.")]
	[InlineData("EnumMetaStackGuardFail.cvl", "Array size exceeds stack allocation safety threshold")]
	[InlineData("EnumMetaNameArgsFail.cvl", "Name expects no arguments.")]
	public void Enum_EnumMetaprogramming_Rejections(string caseName, string expectedError)
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

	[Fact]
	public void AnalyzeProject_Enum_NonExhaustive_Is_Flagged()
	{
		var (asts, context) = AnalyzeProject($"{Category}/NonExhaustiveInternal.cvl");

		var status = Assert.IsType<EnumTypeSymbol>(context.ResolveType("Status"));
		Assert.True(status.IsNonExhaustive);
		Assert.False(status.IsFlags);
		Assert.Equal(3, status.Variants.Count);
	}

	[Fact]
	public void Enum_NonExhaustive_Internal_Stays_Exhaustive()
	{
		var fileName = $"{Category}/NonExhaustiveInternal.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("NonExhaustiveInternal", Category);
		Assert.Equal(0, runCode);
		Assert.Equal("pending", runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void Enum_NonExhaustive_External_Consumer_Defaults_To_Fallback()
	{
		var projectPath = $"{Category}/NonExhaustive";
		var (exitCode, stdout, stderr) = RunCompiler(projectPath);
		AssertCompilationSucceeded(exitCode, stdout, stderr, projectPath);

		var (runCode, runStdout) = ExecuteBinary("NonExhaustive", projectPath);
		Assert.Equal(0, runCode);
		Assert.Equal("fallback", runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void Enum_NonExhaustive_External_Consumer_Requires_Default()
	{
		var projectPath = $"{Category}/NonExhaustiveNoDefault";
		var (exitCode, stdout, stderr) = RunCompiler(projectPath);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("[NonExhaustive] enum 'Status' is consumed from another unit and requires an explicit 'default' or 'case _' branch.", stderr);
	}

	[Fact]
	public void Enum_NonExhaustive_NonVoid_Requires_Terminating_Default()
	{
		var projectPath = $"{Category}/NonExhaustiveBadDefault";
		var (exitCode, stdout, stderr) = RunCompiler(projectPath);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("must terminate with a 'return' but ends in non-terminating statement(s).", stderr);
	}
}
