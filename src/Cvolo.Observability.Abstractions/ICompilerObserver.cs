namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Narrow typed observation contract for compiler/tooling code (§30).
/// Implementations must not throw into business logic and must keep
/// <see cref="IsEnabled"/> cheap (no string/array/hash allocation, §30).
/// </summary>
public interface ICompilerObserver
{
	/// <summary>Cheap gate: is anything recorded for this kind/depth in the current mode? Must not allocate.</summary>
	bool IsEnabled(CompilerEventKind kind, DiagnosticDetail detail);

	/// <summary>Opens a span; disabled observers return the shared no-op scope without allocating (§30).</summary>
	IOperationScope BeginOperation(in OperationStart start);

	/// <summary>Emits one small immutable event; runtime stamps time/sequence. Never throws into the caller (§30, §59).</summary>
	void Emit(in CompilerEvent value);

	/// <summary>
	/// Emits a lifecycle event that must not be lost to the mode gate (§3, §58): session start/end and
	/// recording transitions. Implementations still apply queue and health semantics; a disabled
	/// (no-op) observer records nothing.
	/// </summary>
	void EmitLifecycle(in CompilerEvent value);
}
