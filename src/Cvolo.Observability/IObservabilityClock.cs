namespace Cvolo.Observability;

/// <summary>
/// Monotonic + wall clock abstraction (§4, §61). Durations are always measured on
/// <see cref="Timestamp"/>/<see cref="Frequency"/>; UTC is only for readable timestamps.
/// Different processes never compare absolute monotonic ticks.
/// </summary>
public interface IObservabilityClock
{
	/// <summary>Stable id of this clock domain, written into every event (§4, §61).</summary>
	string ClockId { get; }

	/// <summary>Monotonic ticks per second (Stopwatch frequency semantics).</summary>
	long Frequency { get; }

	/// <summary>Current monotonic timestamp.</summary>
	long Timestamp { get; }

	/// <summary>Current UTC ticks (readable time only; never used for durations).</summary>
	long UtcNowTicks { get; }
}
