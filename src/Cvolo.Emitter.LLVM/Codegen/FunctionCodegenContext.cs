using Cvolo.Analysis.Symbols.Base;
using Cvolo.Emitter.LLVM.Codegen.ControlFlow;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen;

/// <summary>
/// Owns mutable LLVM code-generation state whose lifetime is exactly one emitted function.
/// A fresh instance is created for every user function and synthetic thunk so state cannot leak
/// across function boundaries.
/// </summary>
/// <remarks>
/// Compilation- and module-lifetime state deliberately does not belong here. Keeping that state
/// separate makes ownership, cleanup, unsafe-region tracking, and control-flow bookkeeping local
/// to the function currently being emitted.
/// </remarks>
internal sealed class FunctionCodegenContext
{
	/// <summary>
	/// Maps visible variable names to the LLVM storage or address used to read and write them.
	/// Entries include parameters, locals, captures, and function-visible aliases for globals.
	/// </summary>
	public Dictionary<string, LLVMValueRef> Locals { get; } = [];
	/// <summary>
	/// Tracks the semantic type associated with each entry in <see cref="Locals"/>.
	/// </summary>
	public Dictionary<string, TypeSymbol> VariableTypes { get; } = [];
	/// <summary>
	/// Names whose storage represents heap-owned values and therefore participates in heap cleanup.
	/// </summary>
	public HashSet<string> HeapAllocatedVars { get; } = [];
	/// <summary>
	/// Names whose ownership has already been moved out of the current function scope.
	/// Moved values must not be destroyed again during cleanup.
	/// </summary>
	public HashSet<string> MovedVars { get; } = [];
	/// <summary>
	/// Names for which destruction or disposal has already been emitted.
	/// </summary>
	public HashSet<string> DisposedVars { get; } = [];
	/// <summary>
	/// Current nesting depth of unsafe emission regions. A value greater than zero means the
	/// current emission point is allowed to perform operations guarded by unsafe semantics.
	/// </summary>
	public int UnsafeDepth { get; set; }
	/// <summary>
	/// Indicates that the current function transfers ownership to its caller and therefore needs
	/// the corresponding cleanup behavior on exits.
	/// </summary>
	public bool OwnershipTransferFunction { get; set; }
	/// <summary>Hidden native ABI sret destination for the current function, when present.</summary>
	public LLVMValueRef? NativeSRetPointer { get; set; }
	/// <summary>Resolved native ABI signature plan for the current function, when it crosses a native boundary.</summary>
	public TypeLowering.NativeAbiFunctionPlan? NativeAbiPlan { get; set; }
	/// <summary>
	/// Active loop targets, ordered from the innermost loop outward.
	/// </summary>
	public Stack<LoopCodegenFrame> LoopContexts { get; } = [];
	/// <summary>
	/// Break targets for active switch statements.
	/// </summary>
	public Stack<LLVMBasicBlockRef> SwitchBreaks { get; } = [];
	/// <summary>
	/// Active labeled break targets used to resolve labeled <c>break</c> statements.
	/// </summary>
	public Stack<LabeledBreakCodegenFrame> LabeledBreaks { get; } = [];
}
