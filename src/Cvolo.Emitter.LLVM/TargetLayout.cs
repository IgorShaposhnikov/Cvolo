using System.Runtime.InteropServices;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM;

/// <summary>
/// Resolves the LLVM target triple and its ABI data-layout string, and applies
/// them to a module. Owns no long-lived native handles: every target machine it
/// creates is disposed before returning.
/// </summary>
public sealed class TargetLayout
{
	public sealed record Info(string Triple, string DataLayout);

	/// <summary>
	/// Returns the host triple, or <paramref name="explicitTriple"/> when supplied.
	/// Throws if LLVM was not initialized or returned an empty triple.
	/// </summary>
	public unsafe string ResolveTriple(string? explicitTriple = null)
	{
		if (!string.IsNullOrEmpty(explicitTriple))
			return explicitTriple!;

		var nativeTriple = LLVMSharp.Interop.LLVM.GetDefaultTargetTriple();
		if (nativeTriple == null)
			throw new InvalidOperationException(
				"LLVM GetDefaultTargetTriple returned null. " +
				"Ensure LLVM.InitializeAllTargets()/InitializeAllTargetInfos()/InitializeAllTargetMCs() " +
				"ran before creating any module.");

		string triple;
		try
		{
			triple = Marshal.PtrToStringAnsi((IntPtr)nativeTriple) ?? string.Empty;
		}
		finally
		{
			LLVMSharp.Interop.LLVM.DisposeMessage(nativeTriple);
		}

		if (string.IsNullOrEmpty(triple))
			throw new InvalidOperationException(
				"LLVM returned an empty default triple. The target registry was not initialized.");

		return triple;
	}

	/// <summary>
	/// Creates a target machine for <paramref name="triple"/> and returns its ABI
	/// data-layout string. The machine is disposed before returning; only the
	/// managed string survives.
	/// </summary>
	public unsafe string ResolveDataLayout(
		string triple,
		LLVMCodeGenOptLevel optLevel = LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault)
	{
		var target = LLVMTargetRef.GetTargetFromTriple(triple);
		if (target.Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"LLVM target registry lookup failed for '{triple}'. " +
				"Ensure native target registries are initialized and not pruned by IL trimming.");
		}

		var machine = target.CreateTargetMachine(
			triple,
			cpu: "generic",
			features: "",
			optLevel,
			LLVMRelocMode.LLVMRelocDefault,
			LLVMCodeModel.LLVMCodeModelDefault);

		if (machine.Handle == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"Failed to create target machine for '{triple}'.");
		}

		try
		{
			var dl = machine.CreateTargetDataLayout();
			var dlNative = LLVMSharp.Interop.LLVM.CopyStringRepOfTargetData(dl);
			try
			{
				return Marshal.PtrToStringAnsi((IntPtr)dlNative) ?? string.Empty;
			}
			finally
			{
				LLVMSharp.Interop.LLVM.DisposeMessage(dlNative);
			}
		}
		finally
		{
			LLVMSharp.Interop.LLVM.DisposeTargetMachine(machine);
		}
	}

	/// <summary>
	/// Shorthand: resolves triple + data layout and applies them to a module.
	/// </summary>
	public Info Apply(LLVMModuleRef module, string? explicitTriple = null)
	{
		var triple = ResolveTriple(explicitTriple);
		var dl = ResolveDataLayout(triple);
		module.Target = triple;
		module.DataLayout = dl;
		return new Info(triple, dl);
	}
}
