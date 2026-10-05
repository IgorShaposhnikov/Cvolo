using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Emitter.LLVM.Codegen.TypeLowering;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen;

/// <summary>
/// Owns LLVM emission state whose lifetime spans one complete <see cref="CodeGenerator"/> emission
/// session for a module.
/// </summary>
/// <remarks>
/// This context is intentionally limited to compilation/module-level state: the LLVM objects,
/// semantic services, target information, module-wide symbol registries, and shared type lowering.
/// Mutable state that belongs to one emitted function remains in
/// <see cref="FunctionCodegenContext"/> so function emission cannot accidentally leak locals,
/// ownership state, unsafe depth, or control-flow targets into the next function.
/// </remarks>
internal sealed class CodegenContext
{
	/// <summary>
	/// Creates the module-lifetime code-generation context and the shared internal type-lowering
	/// service backed by this context's aggregate type registry.
	/// </summary>
	/// <param name="llvmContext">LLVM context used to create module-level LLVM objects.</param>
	/// <param name="module">LLVM module receiving generated declarations and definitions.</param>
	/// <param name="builder">Shared LLVM instruction builder used during emission.</param>
	/// <param name="targetLayout">Target layout already selected for this emission session.</param>
	/// <param name="debug">Debug-information helper for this module, or null when <c>--debug</c> was
	/// not requested. Must be created only after the module's data layout is applied.</param>
	public CodegenContext(LLVMContextRef llvmContext, LLVMModuleRef module, LLVMBuilderRef builder, TargetLayout targetLayout, DebugMetadata? debug = null)
	{
		LLVMContext = llvmContext;
		Module = module;
		Builder = builder;
		TargetLayout = targetLayout;
		Debug = debug;

		// The lowering/layout services share the active target pointer width.  Raw-union
		// size/alignment and native-delegate storage must not retain the old 64-bit-only
		// assumptions when a 32-bit target data layout is selected.
		NativePointerBits = TargetLayout.ResolvePointerBits(module.DataLayout);
		Types = new LlvmTypeLowering(LlvmStructTypes, NativePointerBits);
		AggregateLayout = new AggregateLayout(NativePointerBits);
	}

	/// <summary>Native pointer width for the active LLVM data layout.</summary>
	public int NativePointerBits { get; }
	public int NativePointerBytes => NativePointerBits / 8;

	/// <summary>
	/// Native LLVM context that owns the module and builder handles used by this emission session.
	/// </summary>
	public LLVMContextRef LLVMContext { get; }
	/// <summary>
	/// LLVM module currently being populated.
	/// </summary>
	public LLVMModuleRef Module { get; }
	/// <summary>
	/// Shared LLVM instruction builder. Its insertion point changes while functions are emitted,
	/// but the builder object itself has module-emission lifetime.
	/// </summary>
	public LLVMBuilderRef Builder { get; }
	/// <summary>
	/// Target data-layout information associated with the current module.
	/// </summary>
	public TargetLayout TargetLayout { get; }
	/// <summary>
	/// Debug-information helper for this module, or null when <c>--debug</c> was not requested. Every
	/// emitter must treat null as "no debug information" and skip location assignment entirely.
	/// </summary>
	public DebugMetadata? Debug { get; }
	/// <summary>True when this emission session produces debug information.</summary>
	public bool DebugEnabled => Debug is not null;
	/// <summary>
	/// Returns the <see cref="CompilationContext"/> owning the unit currently being emitted, so source
	/// spans can be resolved against the right file. Falls back to the session's primary context.
	/// </summary>
	public CompilationContext? CurrentSource() =>
		CurrentUnit is not null
			&& BindingContext is not null
			&& BindingContext.FileContexts.TryGetValue(CurrentUnit, out var unitContext)
			? unitContext
			: CompilationContext;
	/// <summary>
	/// Converts a source span into the 1-based line and column DWARF expects. Returns false when no
	/// source context is available, in which case the caller must not attach a debug location.
	/// </summary>
	public bool TryGetCoordinates(TextSpan span, out uint line, out uint column)
	{
		var source = CurrentSource();
		if (source is null)
		{
			line = 0;
			column = 0;
			return false;
		}

		var (resolvedLine, resolvedColumn) = source.GetCoordinates(span.Start);
		line = (uint)Math.Max(1, resolvedLine);
		column = (uint)Math.Max(1, resolvedColumn);
		return true;
	}
	/// <summary>
	/// Semantic binding context for the compilation currently being emitted.
	/// It is assigned when emission is supplied with semantic analysis state.
	/// </summary>
	public BindingContext? BindingContext { get; set; }
	/// <summary>
	/// Diagnostic/compilation context associated with the current emission session.
	/// </summary>
	public CompilationContext? CompilationContext { get; set; }
	/// <summary>
	/// Compilation unit whose declarations or bodies are currently being emitted.
	/// </summary>
	public CompilationUnitSyntax? CurrentUnit { get; set; }
	/// <summary>
	/// Module-wide LLVM function/value registry keyed by the compiler's resolved symbol names.
	/// </summary>
	public Dictionary<string, LLVMValueRef> Globals { get; } = [];
	/// <summary>
	/// LLVM function signatures keyed by resolved function name.
	/// </summary>
	public Dictionary<string, LLVMTypeRef> FunctionTypes { get; } = [];
	/// <summary>
	/// Named LLVM aggregate types created for Cvolo structs and unions.
	/// </summary>
	public Dictionary<string, LLVMTypeRef> LlvmStructTypes { get; } = [];
	/// <summary>
	/// Semantic parameter types for emitted functions, used by later call emission.
	/// </summary>
	public Dictionary<string, List<TypeSymbol>> FunctionParameterTypes { get; } = [];
	/// <summary>
	/// Semantic return type for each emitted function.
	/// </summary>
	public Dictionary<string, TypeSymbol> FunctionReturnTypes { get; } = [];
	/// <summary>Target-specific native ABI plans keyed by emitted function name.</summary>
	public Dictionary<string, NativeAbiFunctionPlan> NativeAbiFunctionPlans { get; } = [];
	/// <summary>
	/// LLVM storage for module-level Cvolo global variables.
	/// </summary>
	public Dictionary<string, LLVMValueRef> GlobalVariables { get; } = [];
	/// <summary>
	/// Semantic type for each module-level Cvolo global variable.
	/// </summary>
	public Dictionary<string, TypeSymbol> GlobalVariableTypes { get; } = [];
	/// <summary>Qualified names of globals whose storage is owned by a foreign native library.</summary>
	public HashSet<string> ForeignGlobalNames { get; } = [];
	/// <summary>
	/// Maps short global names to the qualified candidates visible under that short name.
	/// </summary>
	public Dictionary<string, List<string>> GlobalShortNames { get; } = [];
	/// <summary>
	/// Shared semantic-type to internal-LLVM-type lowering service for this module.
	/// </summary>
	public LlvmTypeLowering Types { get; }
	/// <summary>
	/// Shared aggregate layout helpers used by emitters for field indices and the current byte-size rules.
	/// </summary>
	public AggregateLayout AggregateLayout { get; }
}
