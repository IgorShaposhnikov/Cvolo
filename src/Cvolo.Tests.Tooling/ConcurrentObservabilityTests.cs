using System.Collections.Concurrent;
using System.Threading;
using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Concurrency contracts for the tooling observability runtime (OBS-CON-001..010). These tests use
/// deterministic synchronisation primitives (Barrier, ManualResetEventSlim, SpinWait) instead of
/// sleeps, so causality is asserted rather than raced. They verify that concurrent tooling work
/// keeps identities disjoint, that shared lazy computations still execute exactly once under
/// contention, that lock waits are measured without changing lock ordering, and that interleaved
/// sessions never leak events across queues. The remaining concurrency contracts (cancelling a
/// blocked waiter, watchdog escalation, request coalescing) belong to the language-server stage and
/// are tracked there.
/// </summary>
public sealed class ConcurrentObservabilityTests
{
	private const string Source =
		"int add(int a, int b) {\n" +
		"    return a + b;\n" +
		"}\n" +
		"\n" +
		"int main() {\n" +
		"    return add(1, 2);\n" +
		"}\n";

	/// <summary>
	/// Forwards to the real observer and additionally records event codes synchronously, so a test
	/// can observe that a participant reached a specific stage without racing the bounded queue.
	/// </summary>
	private sealed class RecordingObserver : ICompilerObserver
	{
		private readonly ICompilerObserver _inner;

		public RecordingObserver(ICompilerObserver inner) => _inner = inner;

		public ConcurrentQueue<string> Codes { get; } = new();

		public bool IsEnabled(CompilerEventKind kind, DiagnosticDetail detail) => _inner.IsEnabled(kind, detail);

		public IOperationScope BeginOperation(in OperationStart start)
		{
			Codes.Enqueue(start.Event);
			return _inner.BeginOperation(start);
		}

		public void Emit(in CompilerEvent value)
		{
			Codes.Enqueue(value.Event);
			_inner.Emit(value);
		}

		public void EmitLifecycle(in CompilerEvent value)
		{
			Codes.Enqueue(value.Event);
			_inner.EmitLifecycle(value);
		}
	}

	private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver(
		ObservabilityMode mode = ObservabilityMode.Detailed,
		string emitter = "tooling-tests")
	{
		var options = new ObservabilityOptions
		{
			Mode = mode,
			Emitter = emitter,
			QueueCapacity = 100_000,
		};
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(
			options,
			identity: new ObservabilityIdentity(emitter),
			clock: new SystemObservabilityClock(),
			queue: queue);
		return (observer, queue);
	}

	private static List<StampedEvent> Drain(BoundedEventQueue queue)
	{
		var list = new List<StampedEvent>();
		queue.DrainAll(list);
		return list;
	}

	private static string? Attribute(StampedEvent evt, string key)
	{
		if (evt.Event.Attributes is null)
			return null;
		foreach (var pair in evt.Event.Attributes)
		{
			if (pair.Key == key)
				return pair.Value;
		}
		return null;
	}

	private static void SpinUntil(Func<bool> condition, TimeSpan timeout, string message)
	{
		var deadline = DateTime.UtcNow + timeout;
		var spin = new SpinWait();
		while (!condition())
		{
			if (DateTime.UtcNow >= deadline)
				Assert.Fail(message);
			spin.SpinOnce();
		}
	}

	[Fact]
	public void Distinct_snapshots_allocate_distinct_snapshot_identities()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, _) = CreateObserver();

		var first = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var second = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);

		Assert.NotNull(first.InitialSnapshot.SnapshotId);
		Assert.NotNull(second.InitialSnapshot.SnapshotId);
		Assert.NotEqual(first.InitialSnapshot.SnapshotId, second.InitialSnapshot.SnapshotId);
	}

	[Fact]
	public void Parallel_work_on_independent_sessions_keeps_identities_disjoint()
	{
		using var firstFixture = TempProject.Create(("Main.cvl", Source));
		using var secondFixture = TempProject.Create(("Main.cvl", Source));
		var (firstObserver, firstQueue) = CreateObserver(emitter: "session-a");
		var (secondObserver, secondQueue) = CreateObserver(emitter: "session-b");

		var firstProject = CvoloWorkspace.Create(firstObserver).OpenProject(firstFixture.ProjectFilePath);
		var secondProject = CvoloWorkspace.Create(secondObserver).OpenProject(secondFixture.ProjectFilePath);

		using var barrier = new Barrier(2);
		var firstTask = Task.Run(() =>
		{
			barrier.SignalAndWait();
			_ = firstProject.InitialSnapshot.PrepareAnalysisV1();
		});
		var secondTask = Task.Run(() =>
		{
			barrier.SignalAndWait();
			_ = secondProject.InitialSnapshot.PrepareAnalysisV1();
		});
		Task.WaitAll([firstTask, secondTask]);

		var firstSnapshotId = firstProject.InitialSnapshot.SnapshotId;
		var secondSnapshotId = secondProject.InitialSnapshot.SnapshotId;
		Assert.NotEqual(firstSnapshotId, secondSnapshotId);

		// Every event of a session must carry that session's snapshot identity and no other's.
		var firstEvents = Drain(firstQueue);
		var secondEvents = Drain(secondQueue);
		Assert.NotEmpty(firstEvents);
		Assert.NotEmpty(secondEvents);
		Assert.All(firstEvents, e =>
		{
			var id = Attribute(e, "snapshotId");
			Assert.True(id is null || id == firstSnapshotId, $"session-a leaked snapshot {id}");
		});
		Assert.All(secondEvents, e =>
		{
			var id = Attribute(e, "snapshotId");
			Assert.True(id is null || id == secondSnapshotId, $"session-b leaked snapshot {id}");
		});

		// Identity namespaces are session-scoped: raw operation ids (op-1, op-2, ...) legitimately
		// restart in every process/session, and the globally distinguishing identity is the session
		// envelope (emitterInstanceId/sessionId) plus the snapshot id asserted above. What must never
		// happen is one session's event carrying the other session's snapshot identity.
		Assert.NotEqual(firstObserver.Identity.SessionId, secondObserver.Identity.SessionId);
	}

	[Fact]
	public void Concurrent_consumers_of_one_shared_computation_classify_exactly_one_winner()
	{
		var (observer, queue) = CreateObserver();
		var factoryEntered = new ManualResetEventSlim();
		var releaseFactory = new ManualResetEventSlim();
		var calls = 0;
		var lazy = new ObservedLazy<int>(
			() =>
			{
				Interlocked.Increment(ref calls);
				factoryEntered.Set();
				releaseFactory.Wait(Timeout.Infinite);
				return 42;
			},
			observer,
			"analysis",
			"snap-race");

		var winner = Task.Run(() => lazy.Value);
		Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(10)), "the winner must enter the factory");

		// The winner is mid-computation; the second consumer must classify as blocked-on-existing.
		var blocked = Task.Run(() => lazy.Value);

		SpinUntil(() => queue.Count >= 2, TimeSpan.FromSeconds(10),
			"both consumers must publish a lazy discovery event");

		releaseFactory.Set();
		Task.WaitAll([winner, blocked]);

		Assert.Equal(42, winner.Result);
		Assert.Equal(42, blocked.Result);
		Assert.Equal(1, calls); // the shared computation ran exactly once (§19.1.7)

		var events = Drain(queue);
		Assert.Single(events, e => e.Event.Event == "lazy.started_by_consumer");
		Assert.Single(events, e => e.Event.Event == "lazy.waited_for_existing");

		// A later, sequential consumer observes the completed value.
		Assert.Equal(42, lazy.Value);
		Assert.Single(Drain(queue), e => e.Event.Event == "lazy.reused_completed");
	}

	[Fact]
	public void Concurrent_consumers_after_completion_all_reuse_the_cached_value()
	{
		var (observer, queue) = CreateObserver();
		var calls = 0;
		var lazy = new ObservedLazy<int>(
			() =>
			{
				Interlocked.Increment(ref calls);
				return 7;
			},
			observer,
			"analysis",
			"snap-done");

		Assert.Equal(7, lazy.Value); // first consumer completes the computation
		Drain(queue);

		using var barrier = new Barrier(4);
		var consumers = Enumerable.Range(0, 4)
			.Select(_ => Task.Run(() =>
			{
				barrier.SignalAndWait();
				return lazy.Value;
			}))
			.ToArray();
		Task.WaitAll(consumers);

		Assert.All(consumers, t => Assert.Equal(7, t.Result));
		Assert.Equal(1, calls); // never recomputed after completion
		Assert.Equal(4, Drain(queue).Count(e => e.Event.Event == "lazy.reused_completed"));
	}

	[Fact]
	public void Cached_failure_is_shared_under_contention_and_never_reexecuted()
	{
		var (observer, queue) = CreateObserver();
		var calls = 0;
		var lazy = new ObservedLazy<int>(
			() =>
			{
				Interlocked.Increment(ref calls);
				throw new InvalidOperationException("factory failed");
			},
			observer,
			"analysis",
			"snap-fail");

		using var barrier = new Barrier(3);
		var consumers = Enumerable.Range(0, 3)
			.Select(_ => Task.Run(() =>
			{
				barrier.SignalAndWait();
				Assert.Throws<InvalidOperationException>(() => _ = lazy.Value);
			}))
			.ToArray();
		Task.WaitAll(consumers);

		Assert.Equal(1, calls); // the failure is cached, not re-executed (§59.2)
		var events = Drain(queue);
		Assert.Single(events, e => e.Event.Event == "lazy.started_by_consumer");
		Assert.All(
			events.Where(e => e.Event.Event is "lazy.waited_for_existing" or "lazy.failed_cached"),
			e => Assert.Equal("snap-fail", Attribute(e, "snapshotId")));
	}

	[Fact]
	public void Snapshot_advanced_by_WithDocument_attributes_events_to_the_snapshot_that_produced_them()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver();
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var baseSnapshot = project.InitialSnapshot;
		var documentId = project.GetDocumentId("Main.cvl");

		_ = baseSnapshot.PrepareAnalysisV1();
		var derived = baseSnapshot.WithDocument(documentId, SourceText.From("int main() {\n    return 1;\n}\n"));
		_ = derived.PrepareAnalysisV1();

		Assert.NotEqual(baseSnapshot.SnapshotId, derived.SnapshotId);

		var events = Drain(queue);
		var baseStarted = Assert.Single(events, e =>
			e.Event.Event == "lazy.started_by_consumer" && Attribute(e, "snapshotId") == baseSnapshot.SnapshotId);
		var derivedStarted = Assert.Single(events, e =>
			e.Event.Event == "lazy.started_by_consumer" && Attribute(e, "snapshotId") == derived.SnapshotId);
		Assert.Equal($"{baseSnapshot.SnapshotId}:analysis", baseStarted.Event.OperationId);
		Assert.Equal($"{derived.SnapshotId}:analysis", derivedStarted.Event.OperationId);

		// The advanced snapshot owns its own lazy computation; the older snapshot never claims it.
		Assert.All(
			events.Where(e => e.Event.Event == "lazy.started_by_consumer"),
			e => Assert.Contains(
				Attribute(e, "snapshotId"),
				new[] { baseSnapshot.SnapshotId, derived.SnapshotId }));
	}

	[Fact]
	public void Binder_lock_reports_wait_and_hold_times_when_a_waiter_blocks()
	{
		var (inner, queue) = CreateObserver();
		var recording = new RecordingObserver(inner);
		var gate = new object();
		var holderInside = new ManualResetEventSlim();
		var releaseHolder = new ManualResetEventSlim();

		var holder = Task.Run(() => ObservedBinderLock.Run(recording, gate, "binder_context.lock_wait", () =>
		{
			holderInside.Set();
			releaseHolder.Wait(Timeout.Infinite);
			return 1;
		}));
		Assert.True(holderInside.Wait(TimeSpan.FromSeconds(10)), "the holder must acquire the lock first");

		var waiterEntered = new ManualResetEventSlim();
		var waiter = Task.Run(() =>
		{
			waiterEntered.Set();
			return ObservedBinderLock.Run(recording, gate, "binder_context.lock_wait", () => 2);
		});

		// The waiter publishes lock.wait.started and then blocks on acquisition: causality, not a sleep.
		Assert.True(waiterEntered.Wait(TimeSpan.FromSeconds(10)), "the waiter task must start");
		SpinUntil(
			() => recording.Codes.Contains("lock.wait.started"),
			TimeSpan.FromSeconds(10),
			"the waiter must publish lock.wait.started before the holder releases");
		Assert.False(waiter.IsCompleted, "the waiter must still be blocked while the holder is inside the body");

		releaseHolder.Set();
		Task.WaitAll([holder, waiter]);
		Assert.Equal(1, holder.Result);
		Assert.Equal(2, waiter.Result);

		var events = Drain(queue);
		var waits = events.Where(e => e.Event.Event == "lock.wait.completed").ToList();
		Assert.Equal(2, waits.Count); // holder + waiter each recorded acquisition
		Assert.All(waits, e => Assert.Equal("acquired", e.Event.Outcome));
		Assert.All(waits, e => Assert.Equal("binder_context.lock_wait", e.Event.Stage));

		var holds = events.Where(e => e.Event.Event == "lock.hold.completed").ToList();
		Assert.Equal(2, holds.Count);
		Assert.All(holds, e => Assert.Equal("released", e.Event.Outcome));
		Assert.All(holds, e => Assert.NotNull(e.Event.ElapsedUs));

		// Every started wait must be matched by exactly one completed wait.
		Assert.Equal(events.Count(e => e.Event.Event == "lock.wait.started"), waits.Count);
	}

	[Fact]
	public void Reentrant_binder_lock_on_the_same_gate_completes_without_deadlock()
	{
		var (observer, queue) = CreateObserver();
		var gate = new object();

		var task = Task.Run(() => ObservedBinderLock.Run(observer, gate, "binder_context.lock_wait", () =>
			ObservedBinderLock.Run(observer, gate, "binder_context.lock_wait", () => 99)));

		Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "a reentrant lock on the same gate must not deadlock");
		Assert.Equal(99, task.Result);

		var events = Drain(queue);
		Assert.Equal(2, events.Count(e => e.Event.Event == "lock.wait.completed"));
		Assert.Equal(2, events.Count(e => e.Event.Event == "lock.hold.completed"));
	}

	[Fact]
	public void Interleaved_multithreaded_work_publishes_balanced_spans_with_no_catalog_violations()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var options = new ObservabilityOptions
		{
			Mode = ObservabilityMode.Detailed,
			Emitter = "tooling-tests",
			QueueCapacity = 200_000,
		};
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(
			options,
			identity: new ObservabilityIdentity("tooling-tests"),
			clock: new SystemObservabilityClock(),
			queue: queue,
			catalog: catalog);

		var failures = new List<Exception>();
		var failureGate = new object();
		using var barrier = new Barrier(4);
		var tasks = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
		{
			try
			{
				barrier.SignalAndWait();
				for (var iteration = 0; iteration < 3; iteration++)
				{
					using var fixture = TempProject.Create(("Main.cvl", Source));
					var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
					var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
					_ = worker;
					_ = document.GetCompletions(Source.IndexOf("return add(", StringComparison.Ordinal));
					_ = document.GetDocumentSymbols();
					_ = document.GetSemanticTokens();
				}

				return;
			}
			catch (Exception ex)
			{
				lock (failureGate)
					failures.Add(ex);
			}
		})).ToArray();

		Assert.True(Task.WaitAll(tasks, TimeSpan.FromMinutes(2)), "concurrent tooling work must complete");
		Assert.Empty(failures);

		var events = Drain(queue);
		Assert.NotEmpty(events);

		// Every started span must be matched by exactly one terminal with the same operation id.
		var startedById = new Dictionary<string, int>(StringComparer.Ordinal);
		var terminalById = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var evt in events)
		{
			if (evt.Event.OperationId is not { } opId)
				continue;

			if (evt.Event.Outcome == "started" && evt.Event.Event.EndsWith(".started", StringComparison.Ordinal))
			{
				startedById[opId] = startedById.GetValueOrDefault(opId) + 1;
			}
			else if (evt.Event.Event.EndsWith(".completed", StringComparison.Ordinal) ||
				evt.Event.Event.EndsWith(".faulted", StringComparison.Ordinal) ||
				evt.Event.Event.EndsWith(".cancelled", StringComparison.Ordinal))
			{
				terminalById[opId] = terminalById.GetValueOrDefault(opId) + 1;
			}
		}

		Assert.Equal(startedById.Count, terminalById.Count);
		foreach (var (opId, count) in startedById)
			Assert.Equal(count, terminalById.GetValueOrDefault(opId));

		Assert.Equal(0, catalog.UnknownEvents);
		Assert.Equal(0, catalog.UnknownOutcomes);
		Assert.Equal(0, catalog.UnknownReasons);
		Assert.Equal(0, catalog.MissingFields);
	}
}
