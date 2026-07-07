using System.Runtime.InteropServices;
using Cvolo.Emitter.LLVM;
using LLVMSharp.Interop;

public sealed class IrOptimizer(OptimizationLevel level, params string[] additionalFunctions) : ILLVMOptimizer
{
	public void Optimize(LLVMModuleRef module)
	{
		OptimizeInternal(module);
	}

	private void OptimizeInternal(LLVMModuleRef module)
	{
		// If no extra functions are provided, just run the default pipeline
		if (additionalFunctions == null || additionalFunctions.Length == 0)
		{
			OptimizeInternal(module, $"default<{level}>");
			return;
		}

		// 1. Join your custom passes: "mem2reg,dce,instcombine"
		var functionPasses = string.Join(",", additionalFunctions);

		// 2. Wrap them inside an explicit function manager scope
		var customPipeline = $"function({functionPasses})";

		// 3. Chain them sequentially with the default optimization level
		// This runs your custom passes first, then applies the standard pipeline
		var completePipeline = $"{customPipeline},default<{level}>";

		OptimizeInternal(module, completePipeline);
	}

	private unsafe void OptimizeInternal(LLVMModuleRef module, string pipelineDescription = "default<Os>")
	{
		// 1. Initialize ALL core native architectures for flexibility
		LLVM.InitializeAllTargetInfos();
		LLVM.InitializeAllTargets();
		LLVM.InitializeAllTargetMCs();
		LLVM.InitializeAllAsmParsers();
		LLVM.InitializeAllAsmPrinters();

		// 2. Fetch the default target machine layout
		var nativeTriple = LLVM.GetDefaultTargetTriple();
		var triple = Marshal.PtrToStringAnsi((IntPtr)nativeTriple)!;

		// Always free strings returned natively by LLVM target lookups if required by your LLVM version
		LLVM.DisposeMessage(nativeTriple);

		var target = LLVMTargetRef.GetTargetFromTriple(triple);

		var machine = target.CreateTargetMachine(
			triple,
			"generic",
			"",
			LLVMCodeGenOptLevel.LLVMCodeGenLevelAggressive,
			LLVMRelocMode.LLVMRelocDefault,
			LLVMCodeModel.LLVMCodeModelDefault
		);

		// 3. Initialize pass builder options
		var passOptions = LLVM.CreatePassBuilderOptions();

		// 4. Marshal pipeline description and run passes across the module
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
			// 5. CRITICAL: Clean up ALL allocated unmanaged resources
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
