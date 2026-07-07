using System.Runtime.InteropServices;
using Cvolo.Emitter.LLVM;
using LLVMSharp.Interop;

public sealed class IrOptimizer(OptimizationLevel level, params string[] additionalFunctions) : ILLVMOptimizer
{
	private readonly string _compiledPipeline = (additionalFunctions == null || additionalFunctions.Length == 0)
		? $"default<{level}>"
		: $"function({string.Join(",", additionalFunctions)}),default<{level}>";

	static IrOptimizer()
	{
		LLVM.InitializeAllTargetInfos();
		LLVM.InitializeAllTargets();
		LLVM.InitializeAllTargetMCs();
		LLVM.InitializeAllAsmParsers();
		LLVM.InitializeAllAsmPrinters();
	}

	public void Optimize(LLVMModuleRef module)
	{
		OptimizeInternal(module, _compiledPipeline);
	}

	private unsafe void OptimizeInternal(LLVMModuleRef module, string pipelineDescription)
	{
		// 1. Fetch the default target machine layout natively
		var nativeTriple = LLVM.GetDefaultTargetTriple();
		if (nativeTriple == null)
		{
			throw new InvalidOperationException("Failed to retrieve default target triple from LLVM native engine.");
		}

		LLVMTargetMachineRef machine = default;
		try
		{
			// Create a managed string copy only for the target lookup registry
			var tripleString = Marshal.PtrToStringAnsi((IntPtr)nativeTriple)!;
			var target = LLVMTargetRef.GetTargetFromTriple(tripleString);

			// FIX: Pass tripleString here instead of casting nativeTriple
			machine = target.CreateTargetMachine(
				tripleString,
				"generic",
				"",
				LLVMCodeGenOptLevel.LLVMCodeGenLevelAggressive,
				LLVMRelocMode.LLVMRelocDefault,
				LLVMCodeModel.LLVMCodeModelDefault
			);
		}
		finally
		{
			// This remains perfectly safe and still prevents the leak by freeing the native buffer
			LLVM.DisposeMessage(nativeTriple);
		}

		// 2. Initialize pass builder options
		var passOptions = LLVM.CreatePassBuilderOptions();

		// 3. Marshal pipeline description and run passes across the module
		var pPipeline = Marshal.StringToHGlobalAnsi(pipelineDescription);

		try
		{
			LLVMOpaqueError* errorPtr = LLVM.RunPasses(
				module,
				(sbyte*)pPipeline,
				machine,
				passOptions
			);

			if (errorPtr != null)
			{
				var errorRef = new LLVMErrorRef((IntPtr)errorPtr);
				var nativeMsg = LLVM.GetErrorMessage(errorRef);
				var errorMessage = Marshal.PtrToStringAnsi((IntPtr)nativeMsg)!;

				LLVM.DisposeErrorMessage(nativeMsg);
				throw new Exception($"LLVM Pass Engine Failed parsing pipeline '{pipelineDescription}': {errorMessage}");
			}
		}
		finally
		{
			// 4. CRITICAL: Clean up ALL allocated unmanaged resources
			Marshal.FreeHGlobal(pPipeline);
			LLVM.DisposePassBuilderOptions(passOptions);

			// Fixes memory leak: Disposes target machine instance
			if (machine.Handle != IntPtr.Zero)
			{
				LLVM.DisposeTargetMachine(machine);
			}
		}
	}
}
