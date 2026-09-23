using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.TypeLowering;

/// <summary>
/// Resolves declared Cvolo calling conventions to LLVM calling-convention values for a concrete
/// target triple.
/// </summary>
internal static class CallingConventionResolver
{
	/// <summary>
	/// Resolves a declared Cvolo calling convention against the module target triple.
	/// "C" always maps to the platform-native C calling convention. "system" maps to the
	/// hardware/system calling convention of the target family: stdcall on 32-bit Windows,
	/// the Win64 convention on 64-bit Windows, and the default C convention everywhere else.
	/// Unknown conventions fail closed to the platform C convention.
	/// </summary>
	public static uint Resolve(string? declared, string triple)
	{
		if (declared == "system" && triple.Contains("windows", StringComparison.OrdinalIgnoreCase))
		{
			if (triple.Contains("i686", StringComparison.OrdinalIgnoreCase)
				|| triple.Contains("i386", StringComparison.OrdinalIgnoreCase)
				|| triple.StartsWith("x86-windows", StringComparison.OrdinalIgnoreCase)
				|| triple.StartsWith("x86-pc-windows", StringComparison.OrdinalIgnoreCase))
			{
				return (uint)LLVMCallConv.LLVMX86StdcallCallConv;
			}
		}

		return (uint)LLVMCallConv.LLVMCCallConv;
	}
}
