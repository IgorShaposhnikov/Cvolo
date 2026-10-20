using System.Threading;
using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Lazy-analysis observability: how consumers discover a shared computation (started /
/// waited / reused / failed-cached), with unchanged lazy semantics and zero events when
/// observability is off (§19.1.7, §59.1, OBS-COMP shared-lazy traces).
/// </summary>
public sealed class ObservabilityLazyTests
{
	private const string MainText = "int main() {\n    return 0;\n}\n";

	private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateDetailedObserver()
	{
		var options = new ObservabilityOptions { Mode = ObservabilityMode.Detailed, Emitter = "tooling-tests" };
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(options, new ObservabilityIdentity("tooling-tests"), new SystemObservabilityClock(), queue);
		return (observer, queue);
	}

	private static List<StampedEvent> Drain(BoundedEventQueue queue)
	{
		var events = new List<StampedEvent>();
		queue.DrainAll(events);
		return events;
	}

	private static string? Attribute(StampedEvent evt, string key)
	{
		if (evt.Event.Attributes is null)
		{
			return null;
		}

		foreach (var pair in evt.Event.Attributes)
		{
			if (pair.Key == key)
			{
				return pair.Value;
			}
		}

		return null;
	}

	[Fact]
	public void Observed_lazy_reports_started_then_reused_and_keeps_single_execution()
	{
		var (observer, queue) = CreateDetailedObserver();
		var calls = 0;
		var lazy = new ObservedLazy<int>(() => ++calls, observer, "analysis", "snap-t1");

		Assert.Equal(1, lazy.Value);
		Assert.Equal(1, lazy.Value);
		Assert.Equal(1, calls); // semantics: factory ran exactly once

		var events = Drain(queue);
		var started = events.Single(e => e.Event.Event == "lazy.started_by_consumer");
		Assert.Equal("started", started.Event.Outcome);
		Assert.Equal("snap-t1:analysis", started.Event.OperationId);
		Assert.Equal("analysis", Attribute(started, "kind"));
		Assert.Equal("snap-t1", Attribute(started, "snapshotId"));

		var reused = events.Single(e => e.Event.Event == "lazy.reused_completed");
		Assert.Equal("snap-t1", Attribute(reused, "snapshotId"));
		Assert.Equal(2, events.Count);
	}

	[Fact]
	public void Observed_lazy_reports_cached_failure_and_runs_factory_once()
	{
		var (observer, queue) = CreateDetailedObserver();
		var calls = 0;
		var lazy = new ObservedLazy<int>(
			() =>
			{
				calls++;
				throw new InvalidOperationException("factory failed");
			},
			observer,
			"analysis",
			"snap-t2");

		Assert.Throws<InvalidOperationException>(() => _ = lazy.Value);
		Assert.Throws<InvalidOperationException>(() => _ = lazy.Value);
		Assert.Equal(1, calls); // cached failure, no re-execution (Lazy semantics preserved)

		var events = Drain(queue);
		Assert.Contains(events, e => e.Event.Event == "lazy.started_by_consumer");
		var failed = events.Single(e => e.Event.Event == "lazy.failed_cached");
		Assert.Equal("faulted", failed.Event.Outcome);
		Assert.Equal("snap-t2", Attribute(failed, "snapshotId"));
	}

	[Fact]
	public void Consumer_blocked_on_in_flight_computation_reports_waited_for_existing()
	{
		var (observer, queue) = CreateDetailedObserver();
		var factoryEntered = new ManualResetEventSlim();
		var releaseFactory = new ManualResetEventSlim();
		var lazy = new ObservedLazy<int>(
			() =>
			{
				factoryEntered.Set();
				releaseFactory.Wait(Timeout.Infinite);
				return 7;
			},
			observer,
			"analysis",
			"snap-t3");

		var winner = Task.Run(() => lazy.Value);
		Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(10)), "winner must enter the factory");

		// The winner is mid-computation; this consumer must classify as blocked-on-existing.
		var blocked = Task.Run(() => lazy.Value);

		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (queue.Count < 2 && DateTime.UtcNow < deadline)
		{
			Thread.Yield();
		}

		releaseFactory.Set();
		Task.WaitAll([winner, blocked]);
		Assert.Equal(7, winner.Result);
		Assert.Equal(7, blocked.Result);

		var events = Drain(queue);
		Assert.Contains(events, e => e.Event.Event == "lazy.started_by_consumer");
		Assert.Contains(events, e => e.Event.Event == "lazy.waited_for_existing");
	}

	[Fact]
	public void Disabled_observer_leaves_lazy_access_untouched_and_emits_nothing()
	{
		var options = new ObservabilityOptions { Mode = ObservabilityMode.Off, Emitter = "tooling-tests" };
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(options, new ObservabilityIdentity("tooling-tests"), new SystemObservabilityClock(), queue);
		var calls = 0;
		var lazy = new ObservedLazy<int>(() => ++calls, observer, "analysis", null);

		Assert.Equal(1, lazy.Value);
		Assert.Equal(1, lazy.Value);
		Assert.Equal(1, calls);
		Assert.Equal(0, queue.Count); // zero events when off (§59.1)
	}

	[Fact]
	public void Workspace_with_observer_reports_analysis_lifecycle_with_shared_snapshot_id()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, queue) = CreateDetailedObserver();
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		Assert.NotNull(snapshot.SnapshotId); // allocated only because observability is on

		_ = snapshot.PrepareAnalysisV1();
		_ = snapshot.PrepareAnalysisV1();

		var events = Drain(queue)
			.Where(e => e.Event.Event is "lazy.started_by_consumer" or "lazy.reused_completed")
			.ToList();
		var started = events.Single(e => e.Event.Event == "lazy.started_by_consumer");
		Assert.Equal($"{snapshot.SnapshotId}:analysis", started.Event.OperationId);
		Assert.Equal("analysis", Attribute(started, "kind"));
		Assert.All(events, e => Assert.Equal(snapshot.SnapshotId, Attribute(e, "snapshotId")));
		Assert.Single(events, e => e.Event.Event == "lazy.reused_completed"); // second consumer reused
	}

	[Fact]
	public void Snapshot_derived_by_WithDocument_carries_the_observer_and_gets_its_own_id()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, queue) = CreateDetailedObserver();
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var baseSnapshot = project.InitialSnapshot;
		var documentId = project.GetDocumentId("Main.cvl");

		var derived = baseSnapshot.WithDocument(documentId, SourceText.From("int main() {\n    return 1;\n}\n"));
		Assert.Same(observer, derived.Observer); // propagation without statics (§29.4)
		Assert.NotEqual(baseSnapshot.SnapshotId, derived.SnapshotId);

		_ = derived.PrepareAnalysisV1();

		var started = Drain(queue).Single(e => e.Event.Event == "lazy.started_by_consumer");
		Assert.Equal($"{derived.SnapshotId}:analysis", started.Event.OperationId);
	}

	[Fact]
	public void Workspace_without_observer_still_analyzes_unchanged()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

		_ = project.InitialSnapshot.PrepareAnalysisV1();

		Assert.Null(project.InitialSnapshot.SnapshotId); // disabled sessions allocate nothing
		Assert.Same(NoopCompilerObserver.Instance, project.InitialSnapshot.Observer);
	}
}
