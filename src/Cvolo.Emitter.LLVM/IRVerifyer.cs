using System.Runtime.InteropServices;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM;

public sealed class IRVerifier(string diagnosticsDirectory)
{
	public void VerifyModule(LLVMModuleRef module, LLVMModuleRef noOptimizedModule)
	{
		try
		{
			// 1. Trigger the verification on the active module
			module.Verify(LLVMVerifierFailureAction.LLVMReturnStatusAction);
		}
		catch (ExternalException ex)
		{
			var error = ex.Message;
			var finalLogPath = diagnosticsDirectory;

			try
			{
				// Ensure the CompilationDiagnostics directory exists safely
				Directory.CreateDirectory(diagnosticsDirectory);

				// Use a safe, OS-compliant timestamp format (e.g., "llvm_error_20260903_192215.ll")
				string safeTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
				string fileName = $"llvm_error_{safeTimestamp}.ll";

				finalLogPath = Path.Combine(diagnosticsDirectory, fileName);

				// 2. Dump diagnostic data detailing both states of the IR
				using var writer = new StreamWriter(finalLogPath);

				writer.WriteLine("; ======================================================================");
				writer.WriteLine("; INTERNAL COMPILER ERROR: INVALID LLVM IR DETECTED");
				writer.WriteLine($"; Error Details: {error.Replace("\n", "\n; ")}");
				writer.WriteLine("; ======================================================================");
				writer.WriteLine();

				writer.WriteLine("; --- POST-OPTIMIZATION IR (FAILED VERIFICATION) ---");
				writer.WriteLine(module.PrintToString());
				writer.WriteLine();

				if (noOptimizedModule.Handle != IntPtr.Zero)
				{
					writer.WriteLine("; --- PRE-OPTIMIZATION IR (ORIGINAL FRONTEND OUTPUT) ---");
					writer.WriteLine(noOptimizedModule.PrintToString());
				}
			}
			catch (Exception dumpEx)
			{
				// Fallback inside the message string if writing fails completely
				finalLogPath = $"{diagnosticsDirectory} (Failed to write dump: {dumpEx.Message})";
			}

			// 3. Report a high-level error to the user
			var msg = $"Internal Compiler Error: The compiler generated invalid LLVM IR.\n" +
					  $"Error details: {error}\n" +
					  $"A diagnostic dump of the generated IR has been saved to '{finalLogPath}'.\n" +
					  $"Please report this issue to the Cvolo maintainers.";

			Console.WriteLine(msg);

			throw new InvalidOperationException(msg, ex);
		}
	}
}
