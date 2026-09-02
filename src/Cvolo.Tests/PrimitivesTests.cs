using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class PrimitiveTypesTests : CompilerTestBase
{
	[Theory]
	// Signed Integers
	[InlineData("SByte", "-120")]
	[InlineData("Short", "-32000")]
	[InlineData("Int", "10")]
	[InlineData("Long", "9000000000")]
	[InlineData("NInt", "-100")]
	// Unsigned Integers
	[InlineData("Byte", "250")]
	[InlineData("UShort", "65000")]
	[InlineData("UInt", "4000000000")]
	[InlineData("ULong", "18000000000")]
	[InlineData("NUInt", "200")]
	// Floating-Point
	[InlineData("Float", "12.500000")]
	[InlineData("Double", "20.5")]
	// Non-Numeric Primitives
	[InlineData("Bool", "1")]
	[InlineData("Char", "A")]
	public void Primitive(string caseName, string expectedOutput)
	{
		var fileName = $"Primitives/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Primitives");
		Assert.Equal(0, runCode);
		Assert.Contains(expectedOutput.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("ValInference", "100")]
	[InlineData("VarInference", "300")]
	[InlineData("ValType", "400")]
	[InlineData("VarType", "600")]
	public void Variable(string caseName, string expected)
	{
		var fileName = $"VariableDeclarations/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "VariableDeclarations");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}
}
