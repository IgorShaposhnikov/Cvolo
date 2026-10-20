using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Shield around any observer implementation (§30, OBS-IO-013): no exception escapes into
/// business logic — including while a compiler lock is held — and no lock order can be
/// affected because the wrapper itself takes no locks.
/// </summary>
public sealed class GuardedCompilerObserver : ICompilerObserver
{
	private readonly ICompilerObserver _inner;
	private long _faults;

	public GuardedCompilerObserver(ICompilerObserver inner)
	{
		ArgumentNullException.ThrowIfNull(inner);
		_inner = inner;
	}

	/// <summary>Number of swallowed observer faults (health metric, §33).</summary>
	public long Faults => System.Threading.Interlocked.Read(ref _faults);

	public bool IsEnabled(CompilerEventKind kind, DiagnosticDetail detail)
	{
		try
		{
			return _inner.IsEnabled(kind, detail);
		}
		catch (Exception)
		{
			System.Threading.Interlocked.Increment(ref _faults);
			return false;
		}
	}

	public IOperationScope BeginOperation(in OperationStart start)
	{
		try
		{
			return _inner.BeginOperation(start);
		}
		catch (Exception)
		{
			System.Threading.Interlocked.Increment(ref _faults);
			return NoopOperationScope.Instance;
		}
	}

	public void Emit(in CompilerEvent value)
	{
		try
		{
			_inner.Emit(value);
		}
		catch (Exception)
		{
			System.Threading.Interlocked.Increment(ref _faults);
		}
	}

	public void EmitLifecycle(in CompilerEvent value)
	{
		try
		{
			_inner.EmitLifecycle(value);
		}
		catch (Exception)
		{
			System.Threading.Interlocked.Increment(ref _faults);
		}
	}
}
