using Cvolo.Analysis.Symbols.Base;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.TypeLowering;

/// <summary>
/// Emits ABI-affecting LLVM attributes shared by native declarations/definitions and call sites.
/// </summary>
internal static class NativeAbiAttributeEmitter
{
	public static void ApplyBoolFunctionAttributes(
		LLVMContextRef context,
		LLVMValueRef function,
		string? targetTriple,
		TypeSymbol returnType,
		IReadOnlyList<TypeSymbol> parameterTypes,
		int parameterIndexOffset = 0)
	{
		if (!NativeBoolAbiPolicy.UsesZeroExtension(targetTriple))
		{
			return;
		}

		if (returnType.Equals(TypeSymbol.Bool))
		{
			AddEnumFunctionAttribute(context, function, (LLVMAttributeIndex)0, "zeroext");
		}

		for (var i = 0; i < parameterTypes.Count; i++)
		{
			if (parameterTypes[i].Equals(TypeSymbol.Bool))
			{
				AddEnumFunctionAttribute(context, function, (LLVMAttributeIndex)(i + 1 + parameterIndexOffset), "zeroext");
			}
		}
	}

	public static void ApplyBoolCallSiteAttributes(
		LLVMContextRef context,
		LLVMValueRef call,
		string? targetTriple,
		TypeSymbol returnType,
		IReadOnlyList<TypeSymbol> parameterTypes,
		int parameterIndexOffset = 0)
	{
		if (!NativeBoolAbiPolicy.UsesZeroExtension(targetTriple))
		{
			return;
		}

		if (returnType.Equals(TypeSymbol.Bool))
		{
			AddEnumCallSiteAttribute(context, call, (LLVMAttributeIndex)0, "zeroext");
		}

		for (var i = 0; i < parameterTypes.Count; i++)
		{
			if (!parameterTypes[i].Equals(TypeSymbol.Bool))
			{
				continue;
			}

			AddEnumCallSiteAttribute(context, call, (LLVMAttributeIndex)(i + 1 + parameterIndexOffset), "zeroext");
		}
	}

	private static unsafe LLVMAttributeRef CreateEnumAttribute(LLVMContextRef context, string name)
	{
		var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
		fixed (byte* ptr = bytes)
		{
			var kind = LLVMSharp.Interop.LLVM.GetEnumAttributeKindForName((sbyte*)ptr, (nuint)name.Length);
			return kind == 0
				? throw new InvalidOperationException($"LLVM does not expose the required ABI attribute '{name}'.")
				: (LLVMAttributeRef)LLVMSharp.Interop.LLVM.CreateEnumAttribute(context, kind, 0);
		}
	}

	private static void AddEnumFunctionAttribute(LLVMContextRef context, LLVMValueRef function, LLVMAttributeIndex index, string name)
	{
		function.AddAttributeAtIndex(index, CreateEnumAttribute(context, name));
	}

	private static unsafe void AddEnumCallSiteAttribute(LLVMContextRef context, LLVMValueRef call, LLVMAttributeIndex index, string name)
	{
		LLVMSharp.Interop.LLVM.AddCallSiteAttribute(call, index, CreateEnumAttribute(context, name));
	}

	internal static unsafe void AddTypeFunctionAttribute(LLVMContextRef context, LLVMValueRef function, LLVMAttributeIndex index, string name, LLVMTypeRef type)
	{
		var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
		fixed (byte* ptr = bytes)
		{
			var kind = LLVMSharp.Interop.LLVM.GetEnumAttributeKindForName((sbyte*)ptr, (nuint)name.Length);
			if (kind == 0)
			{
				throw new InvalidOperationException($"LLVM does not expose the required ABI type attribute '{name}'.");
			}

			var attribute = LLVMSharp.Interop.LLVM.CreateTypeAttribute(context, kind, type);
			function.AddAttributeAtIndex(index, attribute);
		}
	}

	internal static unsafe void AddTypeCallSiteAttribute(LLVMContextRef context, LLVMValueRef call, LLVMAttributeIndex index, string name, LLVMTypeRef type)
	{
		var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
		fixed (byte* ptr = bytes)
		{
			var kind = LLVMSharp.Interop.LLVM.GetEnumAttributeKindForName((sbyte*)ptr, (nuint)name.Length);
			if (kind == 0)
			{
				throw new InvalidOperationException($"LLVM does not expose the required ABI type attribute '{name}'.");
			}

			var attribute = LLVMSharp.Interop.LLVM.CreateTypeAttribute(context, kind, type);
			LLVMSharp.Interop.LLVM.AddCallSiteAttribute(call, index, attribute);
		}
	}
}
