using System.Threading;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// A lazily-computed value that reports how consumers discovered it (§19.1.7 lazy lifecycle):
/// which consumer started the computation, which consumers blocked on the in-flight one,
/// which consumers reused the completed value, and which consumers re-observed a cached
/// failure. The computation itself keeps <see cref="Lazy{T}"/> semantics exactly — execution
/// and publication by a single winner, cached exceptions — observability never changes
/// semantics. Disabled recording does not emit events or allocate diagnostic payloads.
/// A first access while disabled still tracks the computation state so a later transition
/// to detailed recording cannot invent a new computation (§56, OBS-AUD-002).
/// </summary>
internal sealed class ObservedLazy<T>
{
	private const int StateEmpty = 0;
	private const int StateComputing = 1;
	private const int StateCompleted = 2;
	private const int StateFaulted = 3;

	private readonly Lazy<T> _inner;
	private readonly ICompilerObserver _observer;
	private readonly string _kind;
	private readonly string? _snapshotId;
	private int _state;

	public ObservedLazy(Func<T> factory, ICompilerObserver observer, string kind, string? snapshotId)
	{
		ArgumentNullException.ThrowIfNull(factory);
		_inner = new Lazy<T>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
		_observer = observer;
		_kind = kind;
		_snapshotId = snapshotId;
	}

	/// <summary>
	/// The computed value. The first consumer runs the factory; concurrent consumers block on
	/// that winner; later consumers reuse the cached result (or rethrow the cached failure).
	/// </summary>
	public T Value
	{
		get
		{
			if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L1))
			{
				return ReadWithoutEvents();
			}

			var observed = Volatile.Read(ref _state);
			if (observed == StateEmpty &&
				Interlocked.CompareExchange(ref _state, StateComputing, StateEmpty) == StateEmpty)
			{
				Emit("lazy.started_by_consumer", outcome: "started");
				try
				{
					var value = _inner.Value;
					Volatile.Write(ref _state, StateCompleted);
					return value;
				}
				catch
				{
					Volatile.Write(ref _state, StateFaulted);
					throw;
				}
			}

			// Loser of the CAS, or a later consumer. Classification is by observed state:
			// Computing → blocked on someone else's in-flight computation; Completed/Faulted
			// → reuse of a cached outcome. Never guesses, never changes computation (§59.2).
			Emit(
				observed switch
				{
					StateCompleted => "lazy.reused_completed",
					StateFaulted => "lazy.failed_cached",
					_ => "lazy.waited_for_existing",
				},
				outcome: observed == StateFaulted ? "faulted" : null);
			return _inner.Value;
		}
	}

	/// <summary>
	/// Observe the existing Lazy without emitting. A cold disabled access must claim
	/// StateComputing before entering Lazy.Value: otherwise enabling detailed recording
	/// while that factory is running could incorrectly report a second consumer as the
	/// computation's starter. The cold CAS is performed at most once; no event payload,
	/// timestamp, or disk I/O is produced. This small state bookkeeping is necessary
	/// for truthful mode transitions, including a cached failure.
	/// </summary>
	private T ReadWithoutEvents()
	{
		var observed = Volatile.Read(ref _state);
		if (observed == StateEmpty)
		{
			Interlocked.CompareExchange(ref _state, StateComputing, StateEmpty);
		}

		try
		{
			var value = _inner.Value;
			if (observed != StateCompleted)
			{
				Volatile.Write(ref _state, StateCompleted);
			}
			return value;
		}
		catch
		{
			if (observed != StateFaulted)
			{
				Volatile.Write(ref _state, StateFaulted);
			}
			throw;
		}
	}

	private void Emit(string eventCode, string? outcome)
	{
		// Catalog requires operationId on lazy.started_by_consumer; the id names the
		// (snapshot, kind) computation — a real identity, not a fabricated correlation.
		var operationId = _snapshotId is null ? _kind : $"{_snapshotId}:{_kind}";

		_observer.Emit(new CompilerEvent
		{
			Event = eventCode,
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
			Outcome = outcome,
			OperationId = eventCode == "lazy.started_by_consumer" ? operationId : null,
			Attributes =
			[
				new KeyValuePair<string, string>("kind", _kind),
				new("snapshotId", _snapshotId ?? string.Empty),
			],
		});
	}
}
