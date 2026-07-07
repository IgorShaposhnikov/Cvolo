using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM;

public enum OptimizationLevel
{
	O0, // No optimization
	O1, // Moderate
	O2, // Standard aggressive
	O3, // Maximum aggressive
	Os, // Size optimized
	Oz, // Extreme size reduction
	Og  // Debug friendly optimization
}


public interface ILLVMOptimizer
{
	public void Optimize(LLVMModuleRef module);
}
