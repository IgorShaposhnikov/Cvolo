namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Classifies one operation that semantic validation has determined requires unsafe capability.
/// </summary>
/// <remarks>
/// This is the single compiler-owned unsafe-operation taxonomy. Validation sites record usages
/// through <see cref="ValidationContext.RequireUnsafe"/>; the CVL1001 effectiveness check consumes
/// the same recorded usages instead of maintaining an independent safety authority.
/// </remarks>
internal enum UnsafeUsageKind
{
	/// <summary>Heap/GC allocation (scalar or array) that is only valid in unsafe code.</summary>
	HeapAllocation,

	/// <summary>Raw pointer dereference (<c>*ptr</c>).</summary>
	PointerDereference,

	/// <summary>Address-of a raw memory location (<c>&amp;expr</c>).</summary>
	AddressOf,

	/// <summary>Cast to or between raw pointer types.</summary>
	RawPointerCast,

	/// <summary>The <c>null</c> literal, which is unsafe-only.</summary>
	NullUnsafeLiteral,

	/// <summary>Inline assembly.</summary>
	InlineAsm,

	/// <summary>Invocation of a native delegate value.</summary>
	NativeDelegateInvocation,

	/// <summary>Call to a native-ABI function.</summary>
	NativeAbiCall,

	/// <summary>Call to a raw <c>unsafe fn</c> that is not an encapsulated <c>[UnsafeBody]</c>.</summary>
	RawUnsafeCall,

	/// <summary>Indexing through a raw pointer.</summary>
	RawPointerIndex,

	/// <summary>Access to a field of a raw unsafe union.</summary>
	UnsafeUnionAccess,

	/// <summary>Taking the address of a native function (<c>&amp;Function</c>).</summary>
	NativeFunctionAddress,

	/// <summary>Casts involving native delegates and raw pointers.</summary>
	NativeDelegateCast,
}
