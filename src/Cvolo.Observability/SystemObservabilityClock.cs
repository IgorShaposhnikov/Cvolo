using System.Diagnostics;

namespace Cvolo.Observability;

/// <summary>
/// Real process clock backed by <see cref="Stopwatch.GetTimestamp"/> for monotonic
/// measurements and <see cref="DateTime.UtcNow"/> for wall time (§4, §61).
/// </summary>
public sealed class SystemObservabilityClock : IObservabilityClock
{
	public SystemObservabilityClock(string? clockId = null)
	{
		ClockId = clockId ?? $"clock-{Guid.NewGuid():N}";
	}

	public string ClockId { get; }

	public long Frequency => Stopwatch.Frequency;

	public long Timestamp => Stopwatch.GetTimestamp();

	public long UtcNowTicks => DateTime.UtcNow.Ticks;
}
