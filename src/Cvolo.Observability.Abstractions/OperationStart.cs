namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Parameters for opening an operation scope (§30). A scope is closed by exactly one
/// terminal outcome; elapsed time is measured with a monotonic clock by the observer.
/// </summary>
public readonly struct OperationStart
{
	/// <summary>Canonical event name for the started span, e.g. <c>operation.started</c> or a stage-specific start (§38).</summary>
	public required string Event { get; init; }

	/// <summary>Machine operation/stage name, e.g. <c>bind.project</c> (§8 stage list).</summary>
	public string? Stage { get; init; }

	public CompilerEventKind Kind { get; init; }

	/// <summary>Depth this span is recorded at; gated by the observer's current mode (§31).</summary>
	public DiagnosticDetail Detail { get; init; }

	public EventPriority Priority { get; init; }

	/// <summary>Pre-assigned operation id; when null the observer assigns a fresh one.</summary>
	public string? OperationId { get; init; }

	public string? ParentOperationId { get; init; }

	public string? RequestUid { get; init; }

	public RpcRequestId? RpcRequestId { get; init; }

	public string? AnalysisId { get; init; }

	public string? SnapshotId { get; init; }

	public string? DocumentSessionId { get; init; }

	public long? DocumentVersion { get; init; }

	public IReadOnlyList<KeyValuePair<string, string>>? Attributes { get; init; }
}
