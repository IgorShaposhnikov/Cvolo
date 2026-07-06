using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ModularTests : CompilerTestBase
{
	[Fact]
	public void Compiler_MultiFile_Should_Compile_With_Namespaces()
	{
		// Analyze the entire 'App' directory
		var (ast, context) = AnalyzeProject("Modular/App");

		Assert.NotNull(ast);
		Assert.Equal(2, ast.Count); // Ensure both files were parsed
		Assert.False(context.Diagnostics.HasErrors, "Expected successful cross-file namespace compilation.");
	}

	[Fact]
	public void E2E_MultiFile_Should_Execute_With_Correct_Output()
	{
		// Compile the entire 'App' directory
		var (compileCode, stdout, stderr) = RunCompiler("Modular/App");
		AssertCompilationSucceeded(compileCode, stdout, stderr, "Modular/App");

		// Execute the binary (The output binary takes the name of the folder: 'App')
		var (runCode, runStdout) = ExecuteBinary("App", "Modular/App");

		Assert.Equal(0, runCode);
		Assert.Contains("Modular Vector: 10, 20", runStdout);
	}

	[Fact]
	public void E2E_String_Comparison_Across_Files()
	{
		// 1. Compile the project
		var (compileCode, stdout, stderr) = RunCompiler("Modular/StringComparison");
		AssertCompilationSucceeded(compileCode, stdout, stderr, "Modular/StringComparison");

		// 2. Run the binary
		var (runCode, runStdout) = ExecuteBinary("StringComparison", "Modular/StringComparison");
		Assert.Equal(0, runCode);

		// 3. Assert current behavior (Address-based comparison)
		// Note: If you ever implement global string interning or strcmp-based '==', 
		// you will change this to Assert.Contains("RESULT:MATCH", runStdout);
		Assert.Contains("RESULT:MISMATCH", runStdout);
	}

	[Fact]
	public void E2E_Complex_BankSystem_Generics_Project()
	{
		// 1. Semantic Analysis Check
		// This ensures that SymbolUnits and Namespace contexts are resolving correctly across 7 files.
		var (asts, context) = AnalyzeProject("Generics/BankSystem");

		Assert.NotNull(asts);
		Assert.False(context.Diagnostics.HasErrors, "BankSystem should have no analysis errors.");

		// 2. Full Compilation Check
		// This verifies function deduplication, register numbering, and LLVM type safety (icmp vs fcmp).
		var (exitCode, stdout, stderr) = RunCompiler("Generics/BankSystem");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Generics/BankSystem");

		// 3. Runtime Execution Check
		// This verifies that monomorphized logic (math, swaps, etc.) produces correct values.
		var (runCode, runStdout) = ExecuteBinary("BankSystem", "Generics/BankSystem");

		Assert.Equal(0, runCode);

		// Verifies specialization for double (Formatting and fcmp fix)
		Assert.Contains("Balance: 1800.50", runStdout);

		// Verifies specialization for int (Arithmetic fix)
		Assert.Contains("Balance: 4850", runStdout);

		// Verifies Generic Function Monomorphization worked
		Assert.Contains("After Swap:  a=84, b=42", runStdout);

		// Verifies UTF-8 string encoding worked (Borders/Header)
		Assert.Contains("ACCOUNT INFORMATION", runStdout);
	}
}
