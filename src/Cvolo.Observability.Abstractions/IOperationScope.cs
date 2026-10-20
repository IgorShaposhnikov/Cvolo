namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Opened work span (§30). Closed by exactly one terminal outcome:
/// <see cref="Complete"/> wins over <see cref="Dispose"/>; the first terminal wins,
/// later terminals are ignored (and cannot double-count time).
/// </summary>
public interface IOperationScope : IDisposable
{
	/// <summary>Operation id of this span; valid even when the scope is a no-op (empty string there).</summary>
	string OperationId { get; }

	/// <summary>True once a terminal outcome has been recorded for this scope.</summary>
	bool IsCompleted { get; }

	/// <summary>
	/// Records the single terminal outcome (success, not_found, cancelled, faulted,
	/// superseded, degraded, incomplete — §30). Does not throw into business logic.
	/// </summary>
	void Complete(string outcome, string? reason = null, long? elapsedUs = null, IReadOnlyList<KeyValuePair<string, string>>? attributes = null);
}
