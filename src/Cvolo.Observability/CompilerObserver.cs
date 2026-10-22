using System.Threading;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Runtime observer (§30): mode-gated, allocation-free when disabled, monotonic stamps,
/// non-blocking bounded queue, never throws into business logic (§30, §59).
/// Mode switches take effect for subsequent events without restart (§3, OBS-CORE-008);
/// events already emitted keep the recordingEpoch in force at emit time (§58).
/// </summary>
public sealed class CompilerObserver : ICompilerObserver
{
	private readonly ObservabilityIdentity _identity;
	private readonly IObservabilityClock _clock;
	private readonly SequenceAllocator _sequences;
	private readonly BoundedEventQueue _queue;
	private readonly EventCatalog? _catalog;

	private volatile ObservabilityMode _mode;
	private volatile DiagnosticDetail _maxDetail;
	private long _operationCounter;
	private long _internalFaults;

	public CompilerObserver(
		ObservabilityOptions options,
		ObservabilityIdentity? identity = null,
		IObservabilityClock? clock = null,
		BoundedEventQueue? queue = null,
		EventCatalog? catalog = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		_identity = new ObservabilityIdentity(
			options.Emitter,
			options.SessionId,
			options.ServerInstanceId);
		_clock = clock ?? new SystemObservabilityClock();
		_sequences = new SequenceAllocator();
		_queue = queue ?? new BoundedEventQueue(options.QueueCapacity);
		_catalog = catalog;
		_mode = options.Mode;
		_maxDetail = ObservabilityOptions.MaxDetailFor(options.Mode);
	}

	public ObservabilityIdentity Identity => _identity;

	/// <summary>Attached catalog, when validation counters are enabled (§33 health metrics).</summary>
	public EventCatalog? Catalog => _catalog;

	public ObservabilityMode Mode => _mode;

	public BoundedEventQueue Queue => _queue;

	/// <summary>Observer self-faults (sink errors swallowed per §30); exposed as health metric (§33).</summary>
	public long InternalFaults => Interlocked.Read(ref _internalFaults);

	public bool IsEnabled(CompilerEventKind kind, DiagnosticDetail detail)
	{
		// Cheap gate: no allocation, no formatting (§30).
		return _mode != ObservabilityMode.Off && detail <= _maxDetail;
	}

	/// <summary>
	/// Switches recording mode at runtime and bumps the recording epoch (§3, §57.2).
	/// Emits <c>recording.mode_changed</c> (P0) so the transition itself is traceable;
	/// the writer turns an epoch change into <c>recording.cutoff</c> (§58).
	/// </summary>
	public void SetMode(ObservabilityMode mode)
	{
		var previous = _mode;
		if (previous == mode)
		{
			return;
		}

		var previousEpoch = _identity.TransitionRecordingEpoch(_identity.RecordingEpoch + 1);
		_maxDetail = ObservabilityOptions.MaxDetailFor(mode);
		_mode = mode;

		// The transition itself is always recorded when coming from or going to an
		// enabled mode — including into Off, so an active recording ends traceably (§3, §58).
		EmitLifecycle(new CompilerEvent
		{
			Event = "recording.mode_changed",
			Kind = CompilerEventKind.Lifecycle,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P0,
			Outcome = mode.ToString().ToLowerInvariant(),
			Attributes =
			[
				new KeyValuePair<string, string>("previousMode", previous.ToString().ToLowerInvariant()),
				new KeyValuePair<string, string>("newMode", mode.ToString().ToLowerInvariant()),
				new KeyValuePair<string, string>("previousEpoch", previousEpoch.ToString()),
				new KeyValuePair<string, string>("epoch", _identity.RecordingEpoch.ToString()),
			],
		});
	}

	public IOperationScope BeginOperation(in OperationStart start)
	{
		if (!IsEnabled(start.Kind, start.Detail))
		{
			return NoopOperationScope.Instance;
		}

		try
		{
			var operationId = start.OperationId ?? NextOperationId();
			var scope = new OperationScope(this, operationId, start);
			var started = new CompilerEvent
			{
				Event = start.Event,
				Kind = start.Kind,
				Detail = start.Detail,
				Priority = start.Priority,
				Stage = start.Stage,
				Outcome = "started",
				OperationId = operationId,
				ParentOperationId = start.ParentOperationId,
				RequestUid = start.RequestUid,
				RpcRequestId = start.RpcRequestId,
				AnalysisId = start.AnalysisId,
				SnapshotId = start.SnapshotId,
				DocumentSessionId = start.DocumentSessionId,
				DocumentVersion = start.DocumentVersion,
				Attributes = start.Attributes,
			};
			Emit(started);
			return scope;
		}
		catch (Exception)
		{
			// Instrumentation must never break business logic (§30).
			Interlocked.Increment(ref _internalFaults);
			return NoopOperationScope.Instance;
		}
	}

	public void Emit(in CompilerEvent value)
	{
		if (!IsEnabled(value.Kind, value.Detail))
		{
			return;
		}

		try
		{
			if (_catalog is not null)
			{
				_catalog.CountViolations(value);
			}

			// Strip sensitive metadata *before* the bounded collector sees it.
			// This is a bounded projection of already-materialized attributes;
			// Off mode returns above without inspecting the payload.
			var projected = EventPrivacyFilter.Project(value);

			// §59: monotonic stamp + non-blocking enqueue, no disk I/O.
			var stamped = new StampedEvent(
				projected,
				_sequences.Next(),
				timestampUtcTicks: _clock.UtcNowTicks,
				monotonicTicks: _clock.Timestamp,
				recordingEpoch: _identity.RecordingEpoch);
			_queue.TryEnqueue(stamped);
		}
		catch (Exception)
		{
			// Sink failures are recorded best-effort and never rethrown (§30, §12).
			Interlocked.Increment(ref _internalFaults);
		}
	}

	internal long ElapsedUs(long startTimestamp) => TicksToUs(_clock.Timestamp - startTimestamp, _clock.Frequency);

	internal long ClockTimestamp() => _clock.Timestamp;

	/// <summary>Span terminals bypass the mode gate: a scope opened while enabled must close honestly (§30).</summary>
	internal void EmitTerminal(string eventName, in CompilerEvent terminal) => EmitLifecycle(terminal);

	/// <summary>Lifecycle emit: bypasses the mode gate (so transitions are never lost) but keeps queue/health semantics.</summary>
	public void EmitLifecycle(in CompilerEvent value)
	{
		try
		{
			var stamped = new StampedEvent(
				EventPrivacyFilter.Project(value),
				_sequences.Next(),
				timestampUtcTicks: _clock.UtcNowTicks,
				monotonicTicks: _clock.Timestamp,
				recordingEpoch: _identity.RecordingEpoch);
			_queue.TryEnqueue(stamped);
		}
		catch (Exception)
		{
			Interlocked.Increment(ref _internalFaults);
		}
	}

	/// <summary>Converts monotonic ticks to microseconds on the given clock frequency (§4: never via UTC).</summary>
	public static long TicksToUs(long ticks, long frequency) => frequency <= 0 ? 0 : checked(ticks * 1_000_000L / frequency);

	private string NextOperationId() => $"op-{Interlocked.Increment(ref _operationCounter)}";
}
