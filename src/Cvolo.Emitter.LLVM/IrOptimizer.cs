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
		var tripleString = string.Empty;
		try
		{
			// Create a managed string copy only for the target lookup registry
			tripleString = Marshal.PtrToStringAnsi((IntPtr)nativeTriple)!;

			// Verify native target lookup yields a valid structure
			var target = LLVMTargetRef.GetTargetFromTriple(tripleString);
			if (target.Handle != IntPtr.Zero)
			{
				machine = target.CreateTargetMachine(
					tripleString,
					"generic",
					"",
					LLVMCodeGenOptLevel.LLVMCodeGenLevelAggressive,
					LLVMRelocMode.LLVMRelocDefault,
					LLVMCodeModel.LLVMCodeModelDefault
				);
			}
			else
			{
				throw new InvalidOperationException(
					$"LLVM Target registry lookup failed for target triple '{tripleString}'. " +
					"Ensure native target registries are fully initialized and not pruned by IL trimming.");
			}
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException(
				$"Failed to initialize native target machine for '{tripleString}'. " +
				"This is typically caused by aggressive assembly trimming or single-file deployment environment mismatches.", ex);
		}
		finally
		{
			// Clean up the native string allocation immediately to prevent leaks
			LLVM.DisposeMessage(nativeTriple);
		}

		// Ensure our unmanaged target machine layout is safe to pass to RunPasses
		if (machine.Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"Created target machine handle is null for triple '{tripleString}'. " +
				"Cannot execute optimization passes safely without a valid target layout.");
		}

		// 2. Initialize pass builder options
		var passOptions = LLVM.CreatePassBuilderOptions();
		if (passOptions == null)
		{
			throw new InvalidOperationException("Failed to create LLVM PassBuilderOptions pointer.");
		}

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
			// 4. Clean up unmanaged allocations
			Marshal.FreeHGlobal(pPipeline);
			LLVM.DisposePassBuilderOptions(passOptions);

			if (machine.Handle != IntPtr.Zero)
			{
				LLVM.DisposeTargetMachine(machine);
			}
		}
	}
}
