using System.Collections.ObjectModel;
using System.Threading;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>Machine contract for one event code (§57.1): allowed outcomes/reasons and required fields.</summary>
public sealed class EventDefinition
{
	public required string EventCode { get; init; }

	/// <summary>Owning component: "tooling", "lsp", "compiler", "cli"…</summary>
	public string Component { get; init; } = "tooling";

	/// <summary>Allowed outcome values; null = not yet constrained (unknown-outcome checks skipped).</summary>
	public IReadOnlySet<string>? AllowedOutcomes { get; init; }

	/// <summary>Allowed reason codes; null = not yet constrained.</summary>
	public IReadOnlySet<string>? AllowedReasons { get; init; }

	/// <summary>Fields that must be present for this event code (e.g. "operationId").</summary>
	public IReadOnlyList<string> RequiredFields { get; init; } = [];
}

/// <summary>
/// Event catalog (§57.1, OBS-CORE-003): validates event code/outcome/reason against the
/// registered machine contract. Unknown codes/outcomes/reasons are flagged as such —
/// never reinterpreted as success/not_found (§57.1). The full generated catalog becomes
/// the source of truth later; this registry holds the kernel subset of §38.
/// </summary>
public sealed class EventCatalog
{
	private readonly Dictionary<string, EventDefinition> _byCode;
	private long _unknownEvents;
	private long _unknownOutcomes;
	private long _unknownReasons;
	private long _missingFields;

	public EventCatalog(IEnumerable<EventDefinition> definitions)
	{
		ArgumentNullException.ThrowIfNull(definitions);
		_byCode = new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
		foreach (var definition in definitions)
		{
			Register(definition);
		}
	}

	public IReadOnlyCollection<string> EventCodes => new ReadOnlyCollection<string>([.. _byCode.Keys]);

	public long UnknownEvents => Interlocked.Read(ref _unknownEvents);

	public long UnknownOutcomes => Interlocked.Read(ref _unknownOutcomes);

	public long UnknownReasons => Interlocked.Read(ref _unknownReasons);

	public long MissingFields => Interlocked.Read(ref _missingFields);

	public void Register(EventDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentException.ThrowIfNullOrWhiteSpace(definition.EventCode);
		_byCode[definition.EventCode] = definition;
	}

	public bool TryGet(string eventCode, out EventDefinition definition)
		=> _byCode.TryGetValue(eventCode, out definition!);

	/// <summary>Returns machine violation codes for one event (empty = valid). Does not allocate strings for valid events beyond the list.</summary>
	public IReadOnlyList<string> Validate(in CompilerEvent evt)
	{
		List<string>? violations = null;

		void Add(string violation) => (violations ??= []).Add(violation);

		if (!_byCode.TryGetValue(evt.Event, out var definition))
		{
			Add($"unknown_event:{evt.Event}");
		}
		else
		{
			if (definition.AllowedOutcomes is { } outcomes
				&& evt.Outcome is { } outcome
				&& !outcomes.Contains(outcome))
			{
				Add($"unknown_outcome:{evt.Event}:{outcome}");
			}

			if (definition.AllowedReasons is { } reasons
				&& evt.Reason is { } reason
				&& !reasons.Contains(reason))
			{
				Add($"unknown_reason:{evt.Event}:{reason}");
			}

			foreach (var required in definition.RequiredFields)
			{
				if (!HasField(evt, required))
				{
					Add($"missing_field:{evt.Event}:{required}");
				}
			}
		}

		return violations ?? (IReadOnlyList<string>)[];
	}

	/// <summary>
	/// Hot-path validation counters used when the observer has a catalog attached (§33 health
	/// metrics). Same classification as <see cref="Validate"/> without building a list.
	/// </summary>
	public void CountViolations(in CompilerEvent evt)
	{
		if (!_byCode.TryGetValue(evt.Event, out var definition))
		{
			Interlocked.Increment(ref _unknownEvents);
			return;
		}

		if (definition.AllowedOutcomes is { } outcomes
			&& evt.Outcome is { } outcome
			&& !outcomes.Contains(outcome))
		{
			Interlocked.Increment(ref _unknownOutcomes);
		}

		if (definition.AllowedReasons is { } reasons
			&& evt.Reason is { } reason
			&& !reasons.Contains(reason))
		{
			Interlocked.Increment(ref _unknownReasons);
		}

		foreach (var required in definition.RequiredFields)
		{
			if (!HasField(evt, required))
			{
				Interlocked.Increment(ref _missingFields);
				return;
			}
		}
	}

	private static bool HasField(in CompilerEvent evt, string field) => field switch
	{
		"event" => !string.IsNullOrEmpty(evt.Event),
		"operationId" => evt.OperationId is not null,
		"parentOperationId" => evt.ParentOperationId is not null,
		"requestUid" => evt.RequestUid is not null,
		"analysisId" => evt.AnalysisId is not null,
		"snapshotId" => evt.SnapshotId is not null,
		"stage" => evt.Stage is not null,
		"outcome" => evt.Outcome is not null,
		"reason" => evt.Reason is not null,
		"elapsedUs" => evt.ElapsedUs is not null,
		"documentSessionId" => evt.DocumentSessionId is not null,
		"documentVersion" => evt.DocumentVersion is not null,
		_ => true,
	};

	/// <summary>
	/// Kernel subset of the canonical §38 dictionary with the outcome/reason sets defined
	/// so far; extended (and later generated) as instrumentation lands per stage.
	/// </summary>
	public static EventCatalog CreateKernelCatalog()
	{
		static string[] Outcomes(params string[] values) => values;

		return new EventCatalog(
		[
			new EventDefinition
			{
				EventCode = "session.started",
				Component = "runtime",
			},
			new EventDefinition
			{
				EventCode = "session.ended",
				Component = "runtime",
			},
			new EventDefinition
			{
				EventCode = "recording.mode_changed",
				Component = "runtime",
				AllowedOutcomes = new HashSet<string>(["off", "basic", "detailed", "repro"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "recording.triggered",
				Component = "runtime",
			},
			new EventDefinition
			{
				EventCode = "recording.cutoff",
				Component = "runtime",
			},
			new EventDefinition
			{
				EventCode = "operation.started",
				Component = "runtime",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "operation.completed",
				Component = "runtime",
				AllowedOutcomes = new HashSet<string>(
					Outcomes("success", "not_found", "unsupported", "analysis_unavailable", "source_unavailable", "superseded", "degraded", "incomplete"),
					StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "operation.faulted",
				Component = "runtime",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				AllowedReasons = new HashSet<string>(["exception", "sink_unavailable"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "reason"],
			},
			new EventDefinition
			{
				EventCode = "operation.cancelled",
				Component = "runtime",
				AllowedOutcomes = new HashSet<string>(["cancelled"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "operation.depends_on",
				Component = "runtime",
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "operation.superseded",
				Component = "runtime",
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "trace.events_dropped",
				Component = "runtime",
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "invariant.evaluated",
				Component = "runtime",
			},
			new EventDefinition
			{
				EventCode = "invariant.violated",
				Component = "runtime",
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "lazy.started_by_consumer",
				Component = "tooling",
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "lazy.waited_for_existing",
				Component = "tooling",
			},
			new EventDefinition
			{
				EventCode = "lazy.reused_completed",
				Component = "tooling",
			},
			new EventDefinition
			{
				EventCode = "lazy.failed_cached",
				Component = "tooling",
			},
			new EventDefinition
			{
				EventCode = "parse.document.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "parse.document.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("success", "failed", "incomplete"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "parse.project.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "parse.project.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("success", "incomplete"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "bind.pass.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "bind.pass.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("success", "incomplete"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "bind.pass.faulted",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				AllowedReasons = new HashSet<string>(["exception"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "reason"],
			},
			new EventDefinition
			{
				EventCode = "bind.pass.cancelled",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["cancelled"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "bind.diagnostics_suppressed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["suppressed"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "analysis.degraded",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("analysis_unavailable", "degraded"), StringComparer.Ordinal),
				AllowedReasons = new HashSet<string>(["binder_exception_with_parse_errors"], StringComparer.Ordinal),
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "analysis.faulted",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				AllowedReasons = new HashSet<string>(["exception"], StringComparer.Ordinal),
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "symbols.resolve.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "symbols.resolve.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("found", "not_found"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "symbols.resolve.faulted",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				AllowedReasons = new HashSet<string>(["exception"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "reason"],
			},
			new EventDefinition
			{
				EventCode = "declaration_index.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "declaration_index.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("success"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "navigation.definition.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "navigation.definition.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("found", "not_found"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "navigation.definition.faulted",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				AllowedReasons = new HashSet<string>(["exception"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "reason"],
			},
			new EventDefinition
			{
				EventCode = "navigation.source_map",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("mapped", "unmapped"), StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "definition.mapping.rejected",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["rejected"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "decision.made",
				Component = "tooling",
			},
			new EventDefinition
			{
				EventCode = "lock.wait.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "lock.wait.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["acquired"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "lock.hold.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["released"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "classification.document.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "classification.document.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("success", "not_available"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "semantic_tokens.encoded",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["encoded"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "completion.document.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "completion.document.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("success"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "completion.document.faulted",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome", "reason"],
			},
			new EventDefinition
			{
				EventCode = "signature_help.document.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "signature_help.document.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("success", "not_found"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "signature_help.document.faulted",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome", "reason"],
			},
			new EventDefinition
			{
				EventCode = "document_symbols.started",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "document_symbols.completed",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("success"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "document_symbols.faulted",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome", "reason"],
			},
			new EventDefinition
			{
				EventCode = "decision.completion.recovery",
				Component = "tooling",
				AllowedOutcomes = new HashSet<string>(Outcomes("probe", "fallback", "none"), StringComparer.Ordinal),
				RequiredFields = ["outcome", "reason"],
			},
			new EventDefinition
			{
				EventCode = "compiler.invocation.started",
				Component = "cli",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "compiler.invocation.completed",
				Component = "cli",
				AllowedOutcomes = new HashSet<string>(Outcomes("success"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "compiler.invocation.faulted",
				Component = "cli",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				AllowedReasons = new HashSet<string>(["nonzero_exit_code", "exception"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome", "reason"],
			},
			new EventDefinition
			{
				EventCode = "server.start",
				Component = "lsp",
			},
			new EventDefinition
			{
				EventCode = "server.stop",
				Component = "lsp",
			},
			new EventDefinition
			{
				EventCode = "server.crash_detected",
				Component = "lsp",
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "transport.ready",
				Component = "lsp",
			},
			new EventDefinition
			{
				EventCode = "rpc.request.received",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["received"], StringComparer.Ordinal),
				RequiredFields = ["requestUid"],
			},
			new EventDefinition
			{
				EventCode = "rpc.response.sent",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(Outcomes("success", "error", "cancelled"), StringComparer.Ordinal),
				RequiredFields = ["requestUid", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "rpc.notification.received",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["received"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "document.update.received",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["received"], StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "document.update.applied",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["applied"], StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "document.update.rejected",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["rejected"], StringComparer.Ordinal),
				RequiredFields = ["outcome", "reason"],
			},
			new EventDefinition
			{
				EventCode = "document.did_open",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(Outcomes("applied", "rejected"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "document.did_change.received",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["received"], StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "document.did_change.applied",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["applied"], StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "document.did_change.rejected",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["rejected"], StringComparer.Ordinal),
				RequiredFields = ["outcome", "reason"],
			},
			new EventDefinition
			{
				EventCode = "document.did_close",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(Outcomes("applied", "rejected"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "document.did_save",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(Outcomes("applied", "rejected"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "diagnostics.scheduled",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["scheduled"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "diagnostics.requested",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["requested"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "diagnostics.coalesced",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["coalesced"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "diagnostics.committed",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["committed"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "diagnostics.discarded",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["discarded"], StringComparer.Ordinal),
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "diagnostics.publish_attempt",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["attempted"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "diagnostics.publish_sent",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["sent"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "diagnostics.publish_faulted",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "diagnostics.clear_sent",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["sent"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "decision.diagnostics.publish",
				Component = "lsp",
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "worker.queued",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["queued"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "worker.started",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "analysis.started",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "analysis.completed",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(Outcomes("success", "faulted"), StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "snapshot.created",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["created"], StringComparer.Ordinal),
				RequiredFields = ["snapshotId"],
			},
			new EventDefinition
			{
				EventCode = "snapshot.advanced",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["advanced"], StringComparer.Ordinal),
				RequiredFields = ["snapshotId"],
			},
			new EventDefinition
			{
				EventCode = "snapshot.fallback",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["fallback"], StringComparer.Ordinal),
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "snapshot.freshness_checked",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(Outcomes("fresh", "stale"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "snapshot.capture.started",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "snapshot.capture.completed",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(Outcomes("success", "faulted", "not_found"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "project.discovered",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["discovered"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "generation.changed",
				Component = "core",
				AllowedOutcomes = new HashSet<string>(["changed"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "cancellation.requested",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["requested"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "cancellation.observed",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["observed"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "cancellation.deferred",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["deferred"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "operation.completed_after_cancellation",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["completed"], StringComparer.Ordinal),
			},
			new EventDefinition
			{
				EventCode = "cache.hit",
				Component = "core",
			},
			new EventDefinition
			{
				EventCode = "cache.miss",
				Component = "core",
			},
			new EventDefinition
			{
				EventCode = "cache.invalidated",
				Component = "core",
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "watchdog.slow_operation",
				Component = "lsp",
				RequiredFields = ["stage"],
			},
			new EventDefinition
			{
				EventCode = "request.started",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "request.completed",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(
					Outcomes("success", "not_found", "unsupported", "unavailable", "degraded", "stale_discarded"),
					StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "request.faulted",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "request.cancelled",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["cancelled"], StringComparer.Ordinal),
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "definition.position_mapped",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(["mapped", "unmapped", "invalid"], StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "symbol.lookup.started",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "symbol.lookup.completed",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(Outcomes("found", "not_found"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "symbol.lookup.faulted",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				AllowedReasons = new HashSet<string>(["exception"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "reason"],
			},
			new EventDefinition
			{
				EventCode = "definition.targets.started",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "definition.targets.completed",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(Outcomes("found", "none"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "definition.mapping.started",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "definition.mapping.completed",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(Outcomes("mapped", "partial", "rejected"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "definition.response.completed",
				Component = "lsp.definition",
				AllowedOutcomes = new HashSet<string>(Outcomes("found", "not_found"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "snapshot.advance.started",
				Component = "lsp.adapter",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "snapshot.advance.completed",
				Component = "lsp.adapter",
				AllowedOutcomes = new HashSet<string>(Outcomes("advanced", "unchanged"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "snapshot.advance.fallback",
				Component = "lsp.adapter",
				AllowedOutcomes = new HashSet<string>(["fallback"], StringComparer.Ordinal),
				RequiredFields = ["reason"],
			},
			new EventDefinition
			{
				EventCode = "package_session.opened",
				Component = "lsp.adapter",
				AllowedOutcomes = new HashSet<string>(Outcomes("opened", "existing"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "backend.mapping.completed",
				Component = "lsp.adapter",
				AllowedOutcomes = new HashSet<string>(Outcomes("mapped", "rejected"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "document.resolve.completed",
				Component = "lsp.adapter",
				AllowedOutcomes = new HashSet<string>(Outcomes("resolved", "refreshed", "not_found"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "client.initialized",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(Outcomes("initialized", "rejected"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "capabilities.negotiated",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["negotiated"], StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "configuration.applied",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(Outcomes("applied", "unchanged"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			new EventDefinition
			{
				EventCode = "feature.request.started",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "feature.request.completed",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(
					Outcomes("success", "not_found", "unsupported", "unavailable", "stale_discarded"),
					StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "feature.request.faulted",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "reason"],
			},
			new EventDefinition
			{
				EventCode = "feature.request.cancelled",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(["cancelled"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "reason"],
			},
			new EventDefinition
			{
				EventCode = "feature.backend.started",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(["started"], StringComparer.Ordinal),
				RequiredFields = ["operationId"],
			},
			new EventDefinition
			{
				EventCode = "feature.backend.completed",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(Outcomes("completed"), StringComparer.Ordinal),
				RequiredFields = ["operationId", "outcome"],
			},
			new EventDefinition
			{
				EventCode = "feature.backend.faulted",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(["faulted"], StringComparer.Ordinal),
				RequiredFields = ["operationId", "reason"],
			},
			new EventDefinition
			{
				EventCode = "feature.freshness.checked",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(Outcomes("fresh", "stale"), StringComparer.Ordinal),
				RequiredFields = ["outcome"],
			},
			// A user-authored diagnostic marker is causality-critical P0 metadata.
			// The human note never enters basic/detailed event attributes.
			new EventDefinition
			{
				EventCode = "diagnostic.marker",
				Component = "lsp",
				AllowedOutcomes = new HashSet<string>(["added"], StringComparer.Ordinal),
				RequiredFields = ["outcome", "clientActionId"],
			},
			new EventDefinition
			{
				EventCode = "feature.mapping.rejected",
				Component = "lsp.feature",
				AllowedOutcomes = new HashSet<string>(["rejected"], StringComparer.Ordinal),
				RequiredFields = ["outcome", "reason"],
			},
		]);
	}
}
