using System.IO;
using Cvolo.Tests.Std.Core;

namespace Cvolo.Tests.Std;

public sealed class MathTests : CompilerTestBase
{
	[Theory]
	[InlineData("FacadeResolution", "PopCount=3\nAbs=42\nD.Sqrt=2.000000\nPI=3.141593\nF.Sqrt=5.000000")]
	[InlineData("NoAmbiguity", "User=10\nPopCount=2\nD.Sqrt=2.000000")]
	public void Facade(string caseName, string expected)
	{
		var fileName = $"Math/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Math");
		Assert.Equal(0, runCode);
		Assert.Equal(expected, runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void DoubleBasics()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Math/DoubleBasics.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Math/DoubleBasics.cvl");

		var (runCode, runStdout) = ExecuteBinary("DoubleBasics", "Math");
		Assert.Equal(0, runCode);
		var actual = runStdout.Replace("\r\n", "\n").Trim();
		var expected = string.Join("\n",
			"Sqrt(4.0)=2.000000",
			"Sqrt(2.0)=1.414214",
			"Cbrt(27.0)=3.000000",
			"Pow(2.0,10.0)=1024.000000",
			"FMA(2.0,3.0,4.0)=10.000000",
			"Sin(0.0)=0.000000",
			"Cos(0.0)=1.000000",
			"Tan(0.0)=0.000000",
			"Hypot(3.0,4.0)=5.000000",
			"Exp(0.0)=1.000000",
			"Exp2(1.0)=2.000000",
			"Expm1(0.0)=0.000000",
			"Log(1.0)=0.000000",
			"Log2(2.0)=1.000000",
			"Log10(100.0)=2.000000",
			"Log1p(0.0)=0.000000",
			"Floor(1.5)=1.000000",
			"Ceil(1.5)=2.000000",
			"Trunc(-1.5)=-1.000000",
			"Round(2.5)=3.000000",
			"Round(-2.5)=-3.000000",
			"Min(1.0,2.0)=1.000000",
			"Max(1.0,2.0)=2.000000",
			"CopySign(1.0,-2.0)=-1.000000",
			"Abs(-3.5)=3.500000",
			"Clamp(5.0,0.0,10.0)=5.000000",
			"Clamp(-1.0,0.0,10.0)=0.000000",
			"Clamp(11.0,0.0,10.0)=10.000000",
			"Lerp(0.0,10.0,0.5)=5.000000",
			"Sign(5.0)=1.000000",
			"Sign(0.0)=0.000000",
			"Sign(-5.0)=-1.000000",
			"ToRadians(180.0)=3.141593",
			"ToDegrees(3.14159265358979)=180.000000",
			"Sqrt(9.0f)=3.000000",
			"ToRadians(90.0f)=1.570796");
		Assert.Equal(expected, actual);
	}

	[Fact]
	public void IntBasics()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Math/IntBasics.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Math/IntBasics.cvl");

		var (runCode, runStdout) = ExecuteBinary("IntBasics", "Math");
		Assert.Equal(0, runCode);
		var actual = runStdout.Replace("\r\n", "\n").Trim();
		var expected = string.Join("\n",
			"Abs(-42)=42",
			"Min(3,5)=3",
			"Max(3,5)=5",
			"Clamp(11,0,10)=10",
			"Clamp(-1,0,10)=0",
			"Clamp(5,0,10)=5",
			"Sign(-5)=-1",
			"Sign(0)=0",
			"Sign(5)=1",
			"LeadingZeros(1)=31",
			"LeadingZeros(0)=32",
			"TrailingZeros(1)=0",
			"TrailingZeros(0)=32",
			"PopCount(255)=8",
			"PopCount(0)=0",
			"ByteSwap(0x01020304)=67305985",
			"MaxValue=2147483647",
			"MinValue=-2147483648",
			"Long.Abs(-99L)=99",
			"Long.MaxValue=9223372036854775807",
			"Long.MinValue=-9223372036854775808",
			"UInt.MaxValue=4294967295",
			"UInt.Clamp(5U,0U,10U)=5");
		Assert.Equal(expected, actual);
	}

	[Fact]
	public void RotateIntrinsic_TruncatesShiftAmountToOperandWidth()
	{
		// Byte.RotateLeft(0x81, 1u) must emit llvm.fshl.i8 (shift amount coerced
		// from i32 to i8), not llvm.fshl.i32. Build IR-only at -O0 so the call
		// survives constant folding.
		var (exitCode, stdout, stderr) = RunCompiler("Math/BitOps.cvl", "-O0", "--llvm");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Math/BitOps.cvl");

		var assemblyDir = Path.GetDirectoryName(typeof(CompilerTestBase).Assembly.Location)!;
		var irPath = Path.Combine(assemblyDir, TestCasesDirectory, "_isolated", "Math", "BitOps", "obj", "Debug", "BitOps.ll");
		var ir = File.ReadAllText(irPath);

		Assert.Contains("@llvm.fshl.i8(i8", ir);
		Assert.Contains("@llvm.fshr.i8(i8", ir);
		Assert.DoesNotContain("@llvm.fshl.i32(i32 129", ir);
	}

	[Fact]
	public void BitOps()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Math/BitOps.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Math/BitOps.cvl");

		var (runCode, runStdout) = ExecuteBinary("BitOps", "Math");
		Assert.Equal(0, runCode);
		var actual = runStdout.Replace("\r\n", "\n").Trim();
		var expected = string.Join("\n",
			"Byte.RL=3",
			"Byte.RR=192",
			"UShort.RL=3",
			"UShort.RR=49152",
			"UShort.BS=13330",
			"Int.RL=2",
			"Int.RR=536870912");
		Assert.Equal(expected, actual);
	}

	[Fact]
	public void MathEdge()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Math/MathEdge.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Math/MathEdge.cvl");

		var (runCode, runStdout) = ExecuteBinary("MathEdge", "Math");
		Assert.Equal(0, runCode);
		var actual = runStdout.Replace("\r\n", "\n").Trim();
		var expected = string.Join("\n",
			"IsNaN_SqrtNeg=1",
			"IsInf_LogZero=1",
			"IsNaN_LogNeg=1",
			"IsNaN_Asin2=1",
			"IsInf_Atanh1=1",
			"SignBit_NegZero=1",
			"SignBit_Zero=0",
			"MinNaN=1.000000",
			"MaxNaN=1.000000",
			"AbsIntMin=-2147483648",
			"IsNaN_F_SqrtNeg=1",
			"IsInf_F_LogZero=1",
			"SignBit_F_NegZero=1");
		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData("IsFinite", "NaN=0\nPINf=0\nZero=1\nSub=1\nNorm=1")]
	[InlineData("IsNormal", "NaN=0\nPINf=0\nZero=0\nSub=0\nNorm=1")]
	[InlineData("IsSubnormal", "NaN=0\nPINf=0\nZero=0\nSub=1\nNorm=0")]
	public void Classification(string caseName, string expected)
	{
		var fileName = $"Math/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Math");
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void MathConstants()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Math/MathConstants.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Math/MathConstants.cvl");

		var (runCode, runStdout) = ExecuteBinary("MathConstants", "Math");
		Assert.Equal(0, runCode);
		var actual = runStdout.Replace("\r\n", "\n").Trim();
		var expected = string.Join("\n",
			"PI=3.141593",
			"E=2.718282",
			"Tau=6.283185",
			"IsNaN=1",
			"IsInfPos=1",
			"IsInfNeg=1",
			"SignBitNegInf=1",
			"ByteMax=255",
			"ByteMin=0",
			"SByteMax=127",
			"SByteMin=-128",
			"ShortMax=32767",
			"ShortMin=-32768",
			"UShortMax=65535",
			"IntMax=2147483647",
			"IntMin=-2147483648",
			"UIntMax=4294967295",
			"LongMax=9223372036854775807",
			"LongMin=-9223372036854775808",
			"ULongMax=18446744073709551615");
		Assert.Equal(expected, actual);
	}
}
