namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Small immutable event passed by <c>in</c> to <see cref="ICompilerObserver.Emit"/> (§30, §59).
/// Only the observer runtime stamps wall/monotonic time and producer sequence;
/// absent fields are never filled with fabricated values (§4).
/// </summary>
public readonly struct CompilerEvent
{
	/// <summary>Canonical stable machine event name, e.g. <c>operation.completed</c> (§38).</summary>
	public required string Event { get; init; }

	public CompilerEventKind Kind { get; init; }

	public DiagnosticDetail Detail { get; init; }

	public EventPriority Priority { get; init; }

	/// <summary>Canonical stage name from the tooling stage list (§8), when applicable.</summary>
	public string? Stage { get; init; }

	/// <summary>Structured machine outcome (found/not_found/cancelled/faulted/...) — never prose.</summary>
	public string? Outcome { get; init; }

	/// <summary>Structured machine reason code, when the outcome needs an explaining fact (§9).</summary>
	public string? Reason { get; init; }

	/// <summary>Monotonic-clock duration in microseconds; never derived from UTC subtraction (§4, §8).</summary>
	public long? ElapsedUs { get; init; }

	public string? OperationId { get; init; }

	public string? ParentOperationId { get; init; }

	/// <summary>Globally unique internal request key including server instance (§5).</summary>
	public string? RequestUid { get; init; }

	/// <summary>Typed protocol request id, when observable at the boundary (§21.1).</summary>
	public RpcRequestId? RpcRequestId { get; init; }

	public string? AnalysisId { get; init; }

	public string? SnapshotId { get; init; }

	public string? IndexId { get; init; }

	public string? GenerationId { get; init; }

	public string? DocumentSessionId { get; init; }

	public long? DocumentVersion { get; init; }

	public string? ClientActionId { get; init; }

	/// <summary>
	/// Extra structured string attributes (stable machine values only; never source text in
	/// basic/detailed — privacy classification happens before emit, §60).
	/// The list must already be materialized: observers never enumerate lazily-computed
	/// projections after <c>IsEnabled</c> (§39).
	/// </summary>
	public IReadOnlyList<KeyValuePair<string, string>>? Attributes { get; init; }
}
