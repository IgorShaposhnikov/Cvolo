using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.ControlFlow;

/// <summary>
/// Describes the branch targets associated with one active loop during function emission.
/// </summary>
/// <remarks>
/// Frames are pushed when entering a loop and removed when leaving it. Keeping this information
/// in the function-local code-generation context prevents break/continue targets from leaking
/// across function boundaries.
/// </remarks>
internal sealed class LoopCodegenFrame(string? label, LLVMBasicBlockRef breakBlock, LLVMBasicBlockRef continueBlock)
{
	/// <summary>
	/// Optional source-level label attached to the loop.
	/// </summary>
	public string? Label { get; } = label;
	/// <summary>
	/// LLVM block targeted by <c>break</c> for this loop.
	/// </summary>
	public LLVMBasicBlockRef BreakBlock { get; } = breakBlock;
	/// <summary>
	/// LLVM block targeted by <c>continue</c> for this loop.
	/// </summary>
	public LLVMBasicBlockRef ContinueBlock { get; } = continueBlock;
}
