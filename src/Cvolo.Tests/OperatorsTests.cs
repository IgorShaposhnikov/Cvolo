using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class OperatorsTests : CompilerTestBase
{
	[Theory]
	[InlineData("Add", "30")]
	[InlineData("Sub", "20")]
	[InlineData("Mul", "30")]
	[InlineData("Div", "5")]
	[InlineData("Mod", "1")]
	public void Arithmetic(string caseName, string expected)
	{
		var fileName = $"ArithmeticOperators/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "ArithmeticOperators");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("PrefixInc", "11,11")]
	[InlineData("PostfixInc", "11,10")]
	[InlineData("PrefixDec", "9,9")]
	[InlineData("PostfixDec", "9,10")]
	public void IncrementDecrement(string caseName, string expected)
	{
		var fileName = $"IncrementDecrement/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "IncrementDecrement");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("BitwiseAnd", "8")]
	[InlineData("BitwiseOr", "14")]
	[InlineData("BitwiseXor", "6")]
	[InlineData("BitwiseNot", "-13")]
	public void Bitwise(string caseName, string expected)
	{
		var fileName = $"BitwiseOperators/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "BitwiseOperators");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("AndAssign", "8")]
	[InlineData("OrAssign", "14")]
	[InlineData("XorAssign", "6")]
	[InlineData("LShiftAssign", "16")]
	public void BitwiseCompound(string caseName, string expected)
	{
		var fileName = $"BitwiseCompound/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "BitwiseCompound");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("LShift", "8")]
	[InlineData("RShift", "4")]
	public void Shift(string caseName, string expected)
	{
		var fileName = $"ShiftOperators/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "ShiftOperators");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}
}
