namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Shared no-op scope: a single reusable instance so disabled paths allocate nothing (§30, §39).
/// </summary>
public sealed class NoopOperationScope : IOperationScope
{
	public static NoopOperationScope Instance { get; } = new();

	private NoopOperationScope()
	{
	}

	public string OperationId => string.Empty;

	public bool IsCompleted => false;

	public void Complete(string outcome, string? reason = null, long? elapsedUs = null, IReadOnlyList<KeyValuePair<string, string>>? attributes = null)
	{
	}

	public void Dispose()
	{
	}
}
