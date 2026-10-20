namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Default observer (§30): every call is a no-op, nothing is allocated, nothing is
/// computed. Standalone Tooling consumers without a runtime/writer keep working
/// unchanged (§29, §39).
/// </summary>
public sealed class NoopCompilerObserver : ICompilerObserver
{
	public static NoopCompilerObserver Instance { get; } = new();

	private NoopCompilerObserver()
	{
	}

	public bool IsEnabled(CompilerEventKind kind, DiagnosticDetail detail) => false;

	public IOperationScope BeginOperation(in OperationStart start) => NoopOperationScope.Instance;

	public void Emit(in CompilerEvent value)
	{
	}

	public void EmitLifecycle(in CompilerEvent value)
	{
	}
}
