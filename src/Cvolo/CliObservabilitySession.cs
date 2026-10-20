using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo;

/// <summary>
/// Process-scoped observability session for the command-line entry point. The CLI is its own
/// emitter instance: every invocation gets an independent session identity even when several
/// compiler processes run concurrently, so a collected trace can always be attributed to one
/// process. A JSONL writer is created only when an explicit trace path and a non-off mode are
/// requested; otherwise the session stays in-memory and adds no file-system traffic.
/// </summary>
internal sealed class CliObservabilitySession : IDisposable
{
	internal const string ModeEnvironmentVariable = "CVOLO_OBS_MODE";
	internal const string TracePathEnvironmentVariable = "CVOLO_TRACE_PATH";

	private readonly JsonlEventWriter? _writer;
	private int _stopped;

	private CliObservabilitySession(CompilerObserver observer, JsonlEventWriter? writer)
	{
		Observer = observer;
		_writer = writer;
	}

	internal CompilerObserver Observer { get; }

	internal static CliObservabilitySession Start()
	{
		var mode = ParseMode(Environment.GetEnvironmentVariable(ModeEnvironmentVariable));
		var outputPath = Environment.GetEnvironmentVariable(TracePathEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(outputPath))
			outputPath = null;

		var options = new ObservabilityOptions
		{
			Mode = mode,
			Emitter = "cli",
			OutputPath = outputPath,
			QueueCapacity = 16384,
		};

		var clock = new SystemObservabilityClock();
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(
			options,
			clock: clock,
			queue: queue,
			catalog: EventCatalog.CreateKernelCatalog());

		JsonlEventWriter? writer = null;
		if (outputPath is not null && mode != ObservabilityMode.Off)
			writer = new JsonlEventWriter(queue, observer.Identity, clock, options);

		var session = new CliObservabilitySession(observer, writer);
		session.Emit(session.StartedEvent());
		return session;
	}

	internal static ObservabilityMode ParseMode(string? raw)
		=> raw?.Trim().ToLowerInvariant() switch
		{
			"off" => ObservabilityMode.Off,
			"detailed" => ObservabilityMode.Detailed,
			"repro" => ObservabilityMode.Repro,
			"basic" => ObservabilityMode.Basic,
			_ => ObservabilityMode.Basic,
		};

	/// <summary>
	/// Session-start event for the current process. Exposed for tests that inject an observer and
	/// need the same contract without starting a writer-backed session.
	/// </summary>
	internal static CompilerEvent StartedEventOf(ICompilerObserver observer, ObservabilityIdentity identity)
	{
		return new CompilerEvent
		{
			Event = "session.started",
			Kind = CompilerEventKind.Lifecycle,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P0,
			Attributes =
			[
				new("emitter", identity.Emitter),
				new("sessionId", identity.SessionId),
				new("serverInstanceId", identity.ServerInstanceId),
				new("processId", Environment.ProcessId.ToString()),
			],
		};
	}

	private CompilerEvent StartedEvent() => StartedEventOf(Observer, Observer.Identity);

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _stopped, 1) != 0)
			return;

		var identity = Observer.Identity;
		Emit(new CompilerEvent
		{
			Event = "session.ended",
			Kind = CompilerEventKind.Lifecycle,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P0,
			Attributes =
			[
				new("sessionId", identity.SessionId),
				new("serverInstanceId", identity.ServerInstanceId),
			],
		});

		_writer?.Dispose();
	}

	private void Emit(CompilerEvent evt) => Observer.EmitLifecycle(evt);
}
