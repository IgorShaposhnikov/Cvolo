using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>Parallel producers, bounded queue, batching and overflow/IO failure honesty.</summary>
public class QueueAndWriterTests
{
	[Fact]
	public void Parallel_producers_with_active_writer_produce_valid_jsonl_without_deadlock()
	{
		using var temp = new TempDirectory();
		var clock = new FakeClock();
		var options = new ObservabilityOptions
		{
			Mode = ObservabilityMode.Detailed,
			Emitter = "test",
			QueueCapacity = 50_000,
			OutputPath = temp.File("trace.jsonl"),
			WriteBatchSize = 64,
			FlushInterval = TimeSpan.FromMilliseconds(10),
		};
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var identity = new ObservabilityIdentity("test");
		var observer = new CompilerObserver(options, identity, clock, queue);

		int written;
		using (var writer = new JsonlEventWriter(queue, identity, clock, options))
		{
			const int threads = 6;
			const int perThread = 400;
			var barrier = new Barrier(threads);
			var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
			{
				barrier.SignalAndWait();
				for (var i = 0; i < perThread; i++)
				{
					observer.Emit(ObservabilityTestSupport.Event(
						"operation.completed", outcome: "success"));
				}
			})).ToArray();
			Task.WaitAll(tasks); // would deadlock on a blocking queue design

			writer.Flush();
			written = threads * perThread;

			Assert.False(queue.Statistics.HasLoss);
			Assert.False(writer.IsDegraded);
			Assert.False(writer.ReplayIncomplete);
			Assert.Equal(written, writer.WrittenLines);
		} // stream closed before reading back (writer holds the file open for append)

		var result = JsonlTraceReader.ReadFile(options.OutputPath!);

		Assert.Empty(result.Corrupted);
		Assert.Equal(written, result.Events.Count);
		var sequences = result.Events.Select(e => e.ProducerSequence!.Value).Distinct().OrderBy(s => s).ToArray();
		Assert.Equal(written, sequences.Length);
	}

	[Fact]
	public void Queue_overflow_drops_verbose_first_and_reports_every_loss()
	{
		var queue = new BoundedEventQueue(capacity: 4);

		StampedEvent P(EventPriority p, long seq) => new(
			ObservabilityTestSupport.Event("invariant.evaluated", priority: p, outcome: "success"),
			seq, 0, 0, 0);

		Assert.True(queue.TryEnqueue(P(EventPriority.P0, 1)));
		Assert.True(queue.TryEnqueue(P(EventPriority.P1, 2)));
		Assert.True(queue.TryEnqueue(P(EventPriority.P2, 3)));
		Assert.True(queue.TryEnqueue(P(EventPriority.P1, 4))); // full now

		// Overflow with verbose: oldest P2 evicted first.
		Assert.True(queue.TryEnqueue(P(EventPriority.P1, 5)));

		// Overflow with P0: oldest P1 evicted (P0 protected).
		Assert.True(queue.TryEnqueue(P(EventPriority.P0, 6)));

		var stats = queue.Statistics;
		Assert.Equal(2, stats.DroppedTotal);
		Assert.Equal(1, stats.DroppedP2);
		Assert.Equal(1, stats.DroppedP1);
		Assert.Equal(0, stats.DroppedP0);
		Assert.True(stats.HasLoss);
		Assert.Equal(3, stats.LossP2!.Value.FirstDroppedSequence); // sequence range of loss known (§58)
		Assert.Equal(3, stats.LossP2!.Value.LastDroppedSequence);
		Assert.Equal(2, stats.LossP1!.Value.FirstDroppedSequence); // oldest P1 evicted for P0
		Assert.Equal(2, stats.LossP1!.Value.LastDroppedSequence);

		// Remaining: P0(1), P1(4), P1(5), P0(6) → drain order prioritises P0, FIFO within priority.
		var drained = queue.DrainAll();
		Assert.Equal(new long[] { 1, 6, 4, 5 }, drained.Select(e => e.ProducerSequence).ToArray());
	}

	[Fact]
	public void When_all_priorities_overflow_even_P0_drops_are_counted_never_silent()
	{
		var queue = new BoundedEventQueue(capacity: 2);
		StampedEvent P(EventPriority p, long seq) => new(
			ObservabilityTestSupport.Event("session.started", priority: p),
			seq, 0, 0, 0);

		Assert.True(queue.TryEnqueue(P(EventPriority.P0, 1)));
		Assert.True(queue.TryEnqueue(P(EventPriority.P0, 2)));
		Assert.False(queue.TryEnqueue(P(EventPriority.P0, 3))); // no victim left: dropped, counted

		var stats = queue.Statistics;
		Assert.Equal(1, stats.DroppedP0);
		Assert.Equal(1, stats.LossP0!.Value.DroppedCount);
		Assert.Equal(2, queue.Count);
	}

	[Fact]
	public void Io_failure_degrades_the_writer_without_touching_producers()
	{
		using var temp = new TempDirectory();
		var clock = new FakeClock();
		var options = new ObservabilityOptions
		{
			Mode = ObservabilityMode.Detailed,
			Emitter = "test",
			OutputPath = temp.File("trace.jsonl"),
		};
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var identity = new ObservabilityIdentity("test");
		var observer = new CompilerObserver(options, identity, clock, queue);

		// Seam must be active before construction: the ctor opens the stream immediately (§45.6).
		var writer = new JsonlEventWriter(queue, identity, clock, options, _ => new ThrowingStream());

		// Producers keep working: no exception, no semantics change (§14, OBS-IO-002).
		for (var i = 0; i < 100; i++)
		{
			observer.Emit(ObservabilityTestSupport.Event("operation.completed", outcome: "success"));
		}
		writer.Flush();

		Assert.True(writer.IsDegraded);
		Assert.NotNull(writer.DegradedReason);
		Assert.StartsWith("write_failed:", writer.DegradedReason);
		Assert.True(writer.WriterLost > 0); // loss declared, not silent
		Assert.True(writer.ReplayIncomplete);

		// And emitting still succeeds after degradation.
		observer.Emit(ObservabilityTestSupport.Event("operation.completed", outcome: "success"));
		writer.Dispose();
	}

	[Fact]
	public void Unwritable_output_path_degrades_at_open_without_throwing()
	{
		using var temp = new TempDirectory();

		// Make the parent of the output path a FILE: creating the directory must fail.
		var blocker = temp.File("blocker");
		File.WriteAllText(blocker, "x");
		var options = new ObservabilityOptions
		{
			Mode = ObservabilityMode.Detailed,
			Emitter = "test",
			OutputPath = Path.Combine(blocker, "trace.jsonl"),
		};

		var clock = new FakeClock();
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var identity = new ObservabilityIdentity("test");

		using var writer = new JsonlEventWriter(queue, identity, clock, options);

		Assert.True(writer.IsDegraded);
		Assert.NotNull(writer.DegradedReason);
		Assert.StartsWith("open_failed:", writer.DegradedReason);
	}

	private sealed class ThrowingStream : Stream
	{
		public override bool CanRead => false;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => 0;

		public override long Position { get => 0; set => throw new NotSupportedException(); }

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count)
			=> throw new IOException("disk full (simulated)");
	}
}
