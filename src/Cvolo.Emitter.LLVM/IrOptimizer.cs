using System.Runtime.InteropServices;
using Cvolo.Emitter.LLVM;
using LLVMSharp.Interop;

public sealed class IrOptimizer(TargetLayout targetLayout, OptimizationLevel level, params string[] additionalFunctions) : ILLVMOptimizer
{
	private readonly string _compiledPipeline = (additionalFunctions == null || additionalFunctions.Length == 0)
		? $"default<{level}>"
		: $"function({string.Join(",", additionalFunctions)}),default<{level}>";

	public void Optimize(LLVMModuleRef module)
	{
		OptimizeInternal(module, _compiledPipeline);
	}

	private unsafe void OptimizeInternal(LLVMModuleRef module, string pipelineDescription)
	{
		// Prefer the module's already-applied triple; fall back to host.
		var triple = module.Target;

		if (string.IsNullOrEmpty(triple))
			triple = targetLayout.ResolveTriple();

		var target = LLVMTargetRef.GetTargetFromTriple(triple);
		if (target.Handle == IntPtr.Zero)
			throw new InvalidOperationException($"LLVM target lookup failed for '{triple}'.");

		var machine = target.CreateTargetMachine(
			triple,
			"generic",
			"",
			LLVMCodeGenOptLevel.LLVMCodeGenLevelAggressive,
			LLVMRelocMode.LLVMRelocDefault,
			LLVMCodeModel.LLVMCodeModelDefault);

		if (machine.Handle == IntPtr.Zero)
			throw new InvalidOperationException($"Created target machine handle is null for triple '{triple}'.");

		var passOptions = LLVM.CreatePassBuilderOptions();
		if (passOptions == null)
			throw new InvalidOperationException("Failed to create LLVM PassBuilderOptions pointer.");

		var pPipeline = Marshal.StringToHGlobalAnsi(pipelineDescription);
		try
		{
			LLVMOpaqueError* errorPtr = LLVM.RunPasses(module, (sbyte*)pPipeline, machine, passOptions);
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
			Marshal.FreeHGlobal(pPipeline);
			LLVM.DisposePassBuilderOptions(passOptions);
			if (machine.Handle != IntPtr.Zero)
				LLVM.DisposeTargetMachine(machine);
		}
	}
}
