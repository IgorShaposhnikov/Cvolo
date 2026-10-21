using System.Threading;
using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// OBS-AUD-002: enabling detailed recording must not invent a Lazy factory run
/// which already happened (or is still running) while recording was off.
/// </summary>
public sealed class ObservedLazyModeTransitionTests
{
	private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateOffObserver()
	{
		var options = new ObservabilityOptions { Mode = ObservabilityMode.Off, Emitter = "tooling-tests" };
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(
			options,
			new ObservabilityIdentity("tooling-tests"),
			new SystemObservabilityClock(),
			queue);
		return (observer, queue);
	}

	private static List<StampedEvent> LazyEvents(BoundedEventQueue queue)
	{
		var events = new List<StampedEvent>();
		queue.DrainAll(events);
		return events.Where(e => e.Event.Event.StartsWith("lazy.", StringComparison.Ordinal)).ToList();
	}

	[Fact]
	public void Completed_while_off_is_reused_after_detailed_without_a_fabricated_start()
	{
		var (observer, queue) = CreateOffObserver();
		var executions = 0;
		var lazy = new ObservedLazy<int>(() => ++executions, observer, "analysis", "snap-transition");

		Assert.Equal(1, lazy.Value);
		Assert.Empty(LazyEvents(queue));

		observer.SetMode(ObservabilityMode.Detailed);
		Assert.Equal(1, lazy.Value);
		Assert.Equal(1, executions);

		var events = LazyEvents(queue);
		Assert.Single(events);
		Assert.Equal("lazy.reused_completed", events[0].Event.Event);
		Assert.DoesNotContain(events, e => e.Event.Event == "lazy.started_by_consumer");
	}

	[Fact]
	public void Failure_cached_while_off_remains_a_cached_failure_after_detailed()
	{
		var (observer, queue) = CreateOffObserver();
		var executions = 0;
		var lazy = new ObservedLazy<int>(() =>
		{
			executions++;
			throw new InvalidOperationException("cached failure");
		}, observer, "analysis", "snap-failure");

		Assert.Throws<InvalidOperationException>(() => _ = lazy.Value);
		Assert.Empty(LazyEvents(queue));

		observer.SetMode(ObservabilityMode.Detailed);
		Assert.Throws<InvalidOperationException>(() => _ = lazy.Value);
		Assert.Equal(1, executions);

		var events = LazyEvents(queue);
		Assert.Single(events);
		Assert.Equal("lazy.failed_cached", events[0].Event.Event);
		Assert.Equal("faulted", events[0].Event.Outcome);
		Assert.DoesNotContain(events, e => e.Event.Event == "lazy.started_by_consumer");
	}

	[Fact]
	public void A_consumer_after_activation_waits_for_the_factory_started_while_off()
	{
		var (observer, queue) = CreateOffObserver();
		using var entered = new ManualResetEventSlim(false);
		using var release = new ManualResetEventSlim(false);
		var executions = 0;
		var lazy = new ObservedLazy<int>(() =>
		{
			Interlocked.Increment(ref executions);
			entered.Set();
			release.Wait(Timeout.Infinite);
			return 42;
		}, observer, "analysis", "snap-inflight");

		var initial = Task.Run(() => lazy.Value);
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "disabled consumer did not enter factory");
			observer.SetMode(ObservabilityMode.Detailed);
			var second = Task.Run(() => lazy.Value);

			// The queue also holds recording.mode_changed; a second event means the
			// detailed consumer has reached the Lazy instrumentation before release.
			Assert.True(SpinWait.SpinUntil(() => queue.Count >= 2, TimeSpan.FromSeconds(10)));
			release.Set();
			Assert.Equal(42, second.GetAwaiter().GetResult());
			Assert.Equal(42, initial.GetAwaiter().GetResult());
			Assert.Equal(1, executions);

			var events = LazyEvents(queue);
			Assert.Contains(events, e => e.Event.Event == "lazy.waited_for_existing");
			Assert.DoesNotContain(events, e => e.Event.Event == "lazy.started_by_consumer");
		}
		finally
		{
			release.Set();
		}
	}

	[Fact]
	public void A_cold_lazy_first_accessed_after_activation_still_reports_one_start()
	{
		var (observer, queue) = CreateOffObserver();
		var executions = 0;
		var lazy = new ObservedLazy<int>(() => ++executions, observer, "analysis", "snap-cold");

		observer.SetMode(ObservabilityMode.Detailed);
		Assert.Equal(1, lazy.Value);
		Assert.Equal(1, lazy.Value);
		Assert.Equal(1, executions);

		var events = LazyEvents(queue);
		Assert.Single(events, e => e.Event.Event == "lazy.started_by_consumer");
		Assert.Single(events, e => e.Event.Event == "lazy.reused_completed");
	}
}
