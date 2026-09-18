using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cvolo.Emitter.LLVM;

/// <summary>
/// Resolves LLVMSharp's <c>libLLVM</c> P/Invokes from the application directory on every
/// platform. LLVMSharp's built-in hook only knows Linux and Windows file names and has no
/// macOS branch, so the bundled <c>libLLVM.dylib</c> cannot be located on osx-arm64.
/// LLVMSharp invokes its public <see cref="LLVMSharp.Interop.LLVM.ResolveLibrary"/> event
/// before its own fallback, so subscribing here keeps release payloads self-contained.
/// </summary>
internal static class LLVMNativeLibrary
{
	[ModuleInitializer]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios", Justification = "The emitter owns LLVMSharp native resolution and must configure it before any LLVM type is initialized.")]
	internal static void Initialize()
	{
		LLVMSharp.Interop.LLVM.ResolveLibrary += Resolve;
	}

	private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (!string.Equals(libraryName, "libLLVM", StringComparison.Ordinal))
		{
			return IntPtr.Zero;
		}

		foreach (var candidate in CandidateFileNames())
		{
			var path = Path.Combine(AppContext.BaseDirectory, candidate);
			if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
			{
				return handle;
			}
		}

		return IntPtr.Zero;
	}

	private static IEnumerable<string> CandidateFileNames()
	{
		if (OperatingSystem.IsWindows())
		{
			yield return "LLVM-C.dll";
			yield return "libLLVM.dll";
		}
		else if (OperatingSystem.IsMacOS())
		{
			yield return "libLLVM.dylib";
		}
		else
		{
			yield return "libLLVM.so.20";
			yield return "libLLVM-20";
			yield return "libLLVM.so.1";
			yield return "libLLVM.so";
		}
	}
}
