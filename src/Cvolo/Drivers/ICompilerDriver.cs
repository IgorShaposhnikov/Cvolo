namespace Cvolo.Drivers;

/// <summary>
/// Defines the core contract for orchestrating the compilation, semantic analysis, code-generation, and execution stages.
/// </summary>
public interface ICompilerDriver
{
	/// <summary>
	/// Compiles a Cvolo source file, folder, or project into target LLVM IR and executable binaries.
	/// </summary>
	/// <param name="path">The input path containing Cvolo source files (.cv, .cvl) or a .cvlproj project file.</param>
	/// <param name="llvmOnly">If set to <c>true</c>, exits immediately after generating the textual .ll IR file without linking.</param>
	/// <param name="isShared">If set to <c>true</c>, compiles the target project as a shared library (.dll/.so) instead of a console executable.</param>
	/// <param name="emitIr">If set to <c>true</c>, prints the finalized LLVM IR representation directly to stdout.</param>
	/// <param name="optLevel">The compiler optimization level profile (O0, O1, O2, O3, Os, Oz, Og).</param>
	/// <param name="checkOnly">If set to <c>true</c>, terminates immediately after the Binder passes, skipping code generation and linking.</param>
	/// <param name="runAfterCompile">If set to <c>true</c>, programmatically executes the compiled binary immediately upon success.</param>
	/// <param name="verbose">If set to <c>true</c>, prints detailed compiler instrumentation, targets, and linkage diagnostic details.</param>
	/// <param name="emitLowered">If set to <c>true</c>, prints the compiler-lowered (desugared) Cvolo source directly to stdout and exits early.</param>
	/// <param name="noWarn">Comma-separated diagnostic ids (e.g. "CVL1001") whose warnings are suppressed.</param>
	/// <param name="suppressWarnings">If set to <c>true</c>, all warnings are suppressed from output (used by <c>run</c> by default).</param>
	/// <param name="legacyVisibility">If set to <c>true</c>, disables the visibility system and treats all declarations as public (restores v0.2.0-alpha behavior).</param>
	/// <returns>The exit status code of the compilation pass (0 for success, non-zero for failures).</returns>
	int Compile(string path, bool llvmOnly, bool isShared, bool emitIr, string optLevel, bool checkOnly = false, bool runAfterCompile = false, bool verbose = false, bool emitLowered = false, string? noWarn = null, bool suppressWarnings = false, bool legacyVisibility = false);
}
