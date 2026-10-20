using System.Diagnostics;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>Monotonic clock semantics: fake determinism, real Stopwatch conversions, UTC only for display.</summary>
public class ClockTests
{
	[Fact]
	public void Fake_clock_drives_deterministic_event_stamps()
	{
		var clock = new FakeClock(frequency: 1_000_000); // 1 tick = 1 µs
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);

		clock.AdvanceMicroseconds(100);
		var utcBefore = clock.UtcNowTicks;
		observer.Emit(ObservabilityTestSupport.Event("parse.document.completed", outcome: "success"));
		clock.AdvanceMicroseconds(50);
		observer.Emit(ObservabilityTestSupport.Event("parse.document.completed", outcome: "success"));

		var events = queue.DrainAll();
		Assert.Equal(100, events[0].MonotonicTicks); // stamped at the clock value at emit time
		Assert.Equal(150, events[1].MonotonicTicks); // monotonic ticks advance only via clock (§4)
		Assert.True(events[1].TimestampUtcTicks > events[0].TimestampUtcTicks);
		Assert.Equal(utcBefore, events[0].TimestampUtcTicks); // wall clock frozen until advanced
		Assert.Equal("clock-fake", clock.ClockId); // clock domain id is stable
	}

	[Fact]
	public void Microsecond_conversion_matches_frequency_for_any_frequency()
	{
		// 100 ticks per µs… i.e. frequency = 1_000_000 ticks/s? Use a non-standard frequency:
		const long frequency = 3_000_000; // 3 ticks per microsecond
		var ticks = 30_000; // = 10_000 µs
		var micros = CompilerObserver.TicksToUs(ticks, frequency);
		Assert.Equal(10_000, micros);

		// Zero frequency must not divide by zero (defensive, returns 0).
		Assert.Equal(0, CompilerObserver.TicksToUs(ticks, 0));
	}

	[Fact]
	public void Real_stopwatch_clock_advances_monotonic_and_produces_positive_elapsed()
	{
		var clock = new SystemObservabilityClock();
		Assert.Equal(Stopwatch.Frequency, clock.Frequency);

		var startTicks = clock.Timestamp;
		var startUtc = clock.UtcNowTicks;
		Thread.SpinWait(2_000_000); // some real work
		var endTicks = clock.Timestamp;
		var endUtc = clock.UtcNowTicks;

		Assert.True(endTicks >= startTicks, "monotonic clock must never go backwards");
		Assert.True(endTicks > startTicks, "clock must advance");
		var elapsedMicros = CompilerObserver.TicksToUs(endTicks - startTicks, clock.Frequency);
		Assert.True(elapsedMicros >= 0);
		Assert.True(endUtc >= startUtc);
	}

	[Fact]
	public void Timed_operation_with_real_clock_records_non_negative_elapsed()
	{
		var observer = ObservabilityTestSupport.CreateObserver(
			ObservabilityMode.Detailed, out var queue, clock: new SystemObservabilityClock());

		using (var scope = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
		}))
		{
			Thread.SpinWait(1_000_000);
			scope.Complete("success");
		}

		var events = queue.DrainAll();
		var terminal = events[^1];
		Assert.NotNull(terminal.Event.ElapsedUs);
		Assert.True(terminal.Event.ElapsedUs >= 0);
		Assert.True(terminal.MonotonicTicks >= 0); // stamped from the monotonic clock
	}
}
