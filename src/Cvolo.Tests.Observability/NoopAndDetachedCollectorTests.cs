using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>No-op default and detached collector: zero work when disabled, bounded when unobserved.</summary>
public class NoopAndDetachedCollectorTests
{
	/// <summary>List that fails if anything enumerates it — proves disabled paths never touch payload data.</summary>
	private sealed class ExplodingAttributes : IReadOnlyList<KeyValuePair<string, string>>
	{
		public bool Touched { get; private set; }

		public int Count => Touch();

		public KeyValuePair<string, string> this[int index] => throw new InvalidOperationException("must not index");

		public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
		{
			Touch();
			throw new InvalidOperationException("must not enumerate");
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

		private int Touch()
		{
			Touched = true;
			throw new InvalidOperationException("must not touch");
		}
	}

	[Fact]
	public void Noop_observer_never_enumerates_attributes_or_builds_strings()
	{
		var attributes = new ExplodingAttributes();
		var evt = new CompilerEvent
		{
			Event = "operation.completed",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L2,
			Outcome = "success",
			Attributes = attributes,
		};

		NoopCompilerObserver.Instance.Emit(evt);
		_ = NoopCompilerObserver.Instance.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L2);
		var scope = NoopCompilerObserver.Instance.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L2,
			Attributes = attributes,
		});
		scope.Complete("success");

		Assert.False(attributes.Touched);
	}

	[Fact]
	public void Noop_scope_is_a_single_shared_instance_so_disabled_paths_allocate_nothing()
	{
		var first = NoopCompilerObserver.Instance.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
		});
		var second = NoopCompilerObserver.Instance.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
		});

		Assert.Same(NoopOperationScope.Instance, first);
		Assert.Same(first, second);
	}

	[Fact]
	public void Detached_collector_without_writer_keeps_memory_bounded_by_dropping_and_counting()
	{
		// Collector attached, nobody reads: bounded queue must overflow into counted drops, no hang (§14).
		var observer = ObservabilityTestSupport.CreateObserver(
			ObservabilityMode.Detailed, out var queue, queueCapacity: 64);

		for (var i = 0; i < 1000; i++)
		{
			observer.Emit(ObservabilityTestSupport.Event(
				"invariant.evaluated",
				priority: EventPriority.P2,
				outcome: "success"));
		}

		Assert.Equal(64, queue.Count); // still bounded
		var stats = queue.Statistics;
		Assert.Equal(1000 - 64, stats.DroppedP2);
		Assert.True(stats.HasLoss); // replay can never claim completeness
		Assert.NotNull(stats.LossP2);
		Assert.Equal(1000 - 64, stats.LossP2!.Value.DroppedCount);
	}

	[Fact]
	public void Off_mode_observer_does_not_even_touch_the_queue()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Off, out var queue);
		var before = queue.Statistics;

		for (var i = 0; i < 100; i++)
		{
			observer.Emit(ObservabilityTestSupport.Event("session.started"));
		}

		var after = queue.Statistics;
		Assert.Equal(before.Enqueued, after.Enqueued);
		Assert.Equal(0, queue.Count);
	}
}
