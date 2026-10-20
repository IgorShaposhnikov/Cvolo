using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>Deterministic fake monotonic/wall clock (OBS-CORE-006).</summary>
public sealed class FakeClock : IObservabilityClock
{
	private long _timestamp;
	private long _utcTicks = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc).Ticks;

	public FakeClock(long frequency = 1_000_000, string? clockId = null)
	{
		Frequency = frequency;
		ClockId = clockId ?? "clock-fake";
	}

	public string ClockId { get; }

	public long Frequency { get; }

	public long Timestamp => _timestamp;

	public long UtcNowTicks => _utcTicks;

	public void AdvanceTicks(long ticks) => _timestamp += ticks;

	public void AdvanceMicroseconds(long microseconds)
	{
		_timestamp += microseconds * Frequency / 1_000_000;
		_utcTicks += microseconds * 10;
	}
}

/// <summary>Shared factories for kernel tests.</summary>
public static class ObservabilityTestSupport
{
	public static CompilerObserver CreateObserver(
		ObservabilityMode mode,
		out BoundedEventQueue queue,
		int queueCapacity = 10_000,
		EventCatalog? catalog = null,
		IObservabilityClock? clock = null,
		string emitter = "test")
	{
		var options = new ObservabilityOptions
		{
			Mode = mode,
			Emitter = emitter,
			QueueCapacity = queueCapacity,
		};
		queue = new BoundedEventQueue(queueCapacity);
		return new CompilerObserver(
			options,
			identity: new ObservabilityIdentity(emitter),
			clock: clock ?? new FakeClock(),
			queue: queue,
			catalog: catalog);
	}

	public static List<StampedEvent> DrainAll(this BoundedEventQueue queue)
	{
		var list = new List<StampedEvent>();
		queue.DrainAll(list);
		return list;
	}

	public static CompilerEvent Event(
		string name,
		CompilerEventKind kind = CompilerEventKind.Phase,
		DiagnosticDetail detail = DiagnosticDetail.L0,
		EventPriority priority = EventPriority.P1,
		string? outcome = null,
		string? reason = null,
		string? stage = null,
		string? operationId = null)
		=> new()
		{
			Event = name,
			Kind = kind,
			Detail = detail,
			Priority = priority,
			Outcome = outcome,
			Reason = reason,
			Stage = stage,
			OperationId = operationId,
		};
}

/// <summary>Per-test unique temp directory (deleted on dispose).</summary>
public sealed class TempDirectory : IDisposable
{
	public TempDirectory()
	{
		Path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			"cvolo-obs-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path);
	}

	public string Path { get; }

	public string File(string name) => System.IO.Path.Combine(Path, name);

	public void Dispose()
	{
		try
		{
			Directory.Delete(Path, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
