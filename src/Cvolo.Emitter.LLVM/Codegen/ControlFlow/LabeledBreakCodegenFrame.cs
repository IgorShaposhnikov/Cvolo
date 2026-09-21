using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.ControlFlow;

/// <summary>
/// Describes one active labeled break target during function emission.
/// </summary>
/// <remarks>
/// Labeled blocks and labeled loops use the same stack discipline: the innermost matching frame
/// determines the destination for a labeled <c>break</c> statement.
/// </remarks>
internal sealed class LabeledBreakCodegenFrame(string label, LLVMBasicBlockRef breakBlock)
{
	/// <summary>
	/// Source-level label used to resolve a labeled <c>break</c> statement.
	/// </summary>
	public string Label { get; } = label;
	/// <summary>
	/// LLVM block that receives control when the label is broken out of.
	/// </summary>
	public LLVMBasicBlockRef BreakBlock { get; } = breakBlock;
}
