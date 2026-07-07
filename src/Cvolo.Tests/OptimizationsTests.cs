using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class OptimizationsTests : CompilerTestBase
{
	[Fact]
	public void E2E_O3_Optimization_Passes_Should_Succeed()
	{
		// 1. Compile 'opt_demo.cv' with optimizations active
		var fileName = "Optimizations/Optimize.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		// 2. Locate the generated LLVM IR (.ll) file in the obj/Debug folder
		var assemblyDir = Path.GetDirectoryName(typeof(OptimizationsTests).Assembly.Location)!;
		var llPath = Path.Combine(assemblyDir, "TestCases", "Optimizations", "obj", "Debug", "Optimize.ll");

		Assert.True(File.Exists(llPath), $"Expected generated LLVM IR file at '{llPath}' but it was missing.");
		var irContent = File.ReadAllText(llPath);

		// 3. IR-LEVEL VERIFICATIONS (Verifying that optimizations actually took place!)

		// VERIFICATION A: Verify that all stack allocations are promoted to CPU registers.
		// There must be 0 'alloca' instructions left in the optimized LLVM IR.
		Assert.DoesNotContain("alloca", irContent);

		// VERIFICATION B: Verify that the function call to 'compute' was completely inlined and constant-folded.
		// There must be no 'call i32 @compute' or 'call i32 @"...compute"' instructions left in the file.
		Assert.DoesNotContain("call i32 @compute", irContent);
		Assert.DoesNotContain("call i32 @\"Optimizations.compute\"", irContent);

		// VERIFICATION C: Verify that the constant value '150' was backed directly into printf
		Assert.Contains("150", irContent);

		// 4. Run the optimized executable to verify functional correctness
		var (runCode, runStdout) = ExecuteBinary("Optimize", "Optimizations");

		Assert.Equal(0, runCode);
		Assert.Contains("Result: 150", runStdout);
	}
}
