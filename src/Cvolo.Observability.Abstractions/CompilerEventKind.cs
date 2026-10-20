namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Coarse classification of an observability event (§30). Used for cheap gating:
/// <see cref="ICompilerObserver.IsEnabled"/> must not allocate or format anything.
/// </summary>
public enum CompilerEventKind
{
	/// <summary>Session/recording lifecycle: session.started, recording.mode_changed, session.ended.</summary>
	Lifecycle,

	/// <summary>Work-unit spans: operation.started/completed/faulted/cancelled, lock.wait, analysis phases (§38).</summary>
	Phase,

	/// <summary>Decision provenance: decision.made, definition.mapping.rejected (§32).</summary>
	Decision,

	/// <summary>Per-request protocol flow: request.received/scheduled/response.sent (§9).</summary>
	Request,

	/// <summary>Cancellation and supersession: cancellation.*, operation.superseded (§11).</summary>
	Cancellation,

	/// <summary>Degradation/fallback: analysis.degraded, snapshot.fallback (§12).</summary>
	Degradation,

	/// <summary>Numeric observations: histograms, counters, percentiles (§33).</summary>
	Metric,

	/// <summary>Causal links for shared computations: operation.depends_on, request.waits_for_analysis (§5, §30.1).</summary>
	Correlation,

	/// <summary>Runtime invariant evaluation: invariant.evaluated/violated, assertion.evaluated (§23).</summary>
	Assertion,

	/// <summary>Observability self-health: trace.events_dropped, writer degraded status (§14).</summary>
	Health,
}
