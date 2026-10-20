using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>File rotation/retention, crash tails and honest shutdown flushing.</summary>
public class RotationAndCorruptionTests
{
	private static (CompilerObserver Observer, BoundedEventQueue Queue, ObservabilityOptions Options) CreateTraceOptions(
		string outputPath,
		long maxFileSizeBytes,
		int maxRotatedFiles = 4)
	{
		var options = new ObservabilityOptions
		{
			Mode = ObservabilityMode.Detailed,
			Emitter = "test",
			OutputPath = outputPath,
			MaxFileSizeBytes = maxFileSizeBytes,
			MaxRotatedFiles = maxRotatedFiles,
			WriteBatchSize = 32,
		};
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var identity = new ObservabilityIdentity("test");
		var observer = new CompilerObserver(options, identity, new FakeClock(), queue);
		return (observer, queue, options);
	}

	[Fact]
	public void Rotation_respects_file_and_total_size_limits_with_retention()
	{
		using var temp = new TempDirectory();
		var path = temp.File("trace.jsonl");
		var (observer, queue, options) = CreateTraceOptions(path, maxFileSizeBytes: 4 * 1024, maxRotatedFiles: 3);

		using (var writer = new JsonlEventWriter(queue, observer.Identity, new FakeClock(), options))
		{
			for (var i = 0; i < 400; i++)
			{
				observer.Emit(ObservabilityTestSupport.Event(
					"operation.completed",
					outcome: "success",
					reason: $"event-index-{i}-payload-{new string('x', 60)}"));
			}
			writer.Flush();
		}

		var files = Directory.GetFiles(temp.Path, "trace.jsonl*");
		Assert.True(files.Length > 1, "expected rotation to produce multiple files");
		Assert.True(files.Length <= options.MaxRotatedFiles + 1, "retention must bound rotated file count (§IO-003)");

		foreach (var file in files)
		{
			var info = new FileInfo(file);
			// The current file may exceed the limit by at most one already-written line policy-wise;
			// rotated files must be around the limit. Assert a generous hard bound instead of exact sizes.
			Assert.True(info.Length <= options.MaxFileSizeBytes + 8 * 1024,
				$"{file} ({info.Length} bytes) far exceeds per-file limit {options.MaxFileSizeBytes}");
		}

		// Every retained file is individually parseable: corruption in one line never poisons others.
		foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
		{
			var result = JsonlTraceReader.ReadFile(file);
			Assert.Empty(result.Corrupted);
			Assert.NotEmpty(result.Events);
		}
	}

	[Fact]
	public void Torn_last_line_after_crash_is_reported_as_corrupted_tail_with_valid_rest()
	{
		using var temp = new TempDirectory();
		var path = temp.File("trace.jsonl");
		var (observer, queue, options) = CreateTraceOptions(path, maxFileSizeBytes: 1024 * 1024);

		using (var writer = new JsonlEventWriter(queue, observer.Identity, new FakeClock(), options))
		{
			for (var i = 0; i < 10; i++)
			{
				observer.Emit(ObservabilityTestSupport.Event("operation.completed", outcome: "success"));
			}
			writer.Flush();
		}

		// Simulate power loss: last line cut mid-JSON.
		var content = File.ReadAllText(path);
		File.WriteAllText(path, content[..^20]);

		var result = JsonlTraceReader.ReadFile(path);
		Assert.Equal(9, result.Events.Count);
		var corruption = Assert.Single(result.Corrupted);
		Assert.Equal(10, corruption.LineNumber);
		Assert.StartsWith("invalid_json", corruption.Reason); // analyzer reads usable lines + signals the tail (§IO-003)
	}

	[Fact]
	public void Sequence_gaps_from_losses_are_reported_not_hidden()
	{
		var text = string.Join("\n", new[]
		{
			"""{"schemaVersion":2,"emitter":"test","event":"a","producerSequence":1}""",
			"""{"schemaVersion":2,"emitter":"test","event":"a","producerSequence":2}""",
			// 3..5 lost
			"""{"schemaVersion":2,"emitter":"test","event":"a","producerSequence":6}""",
			"""{"schemaVersion":2,"emitter":"other","event":"a","producerSequence":1}""",
		});

		var result = JsonlTraceReader.ReadText(text);
		var gaps = result.SequenceGaps();

		var gap = Assert.Single(gaps); // other emitter has no gap
		Assert.Equal("test", gap.Emitter);
		Assert.Equal(3, gap.FirstMissing);
		Assert.Equal(5, gap.LastMissing);
		Assert.Equal(3, gap.Count);
	}

	[Fact]
	public void Graceful_shutdown_flushes_everything_within_the_deadline()
	{
		using var temp = new TempDirectory();
		var path = temp.File("trace.jsonl");
		var (observer, queue, options) = CreateTraceOptions(path, maxFileSizeBytes: 1024 * 1024);
		options.ShutdownFlushDeadline = TimeSpan.FromSeconds(5);

		var writer = new JsonlEventWriter(queue, observer.Identity, new FakeClock(), options);
		for (var i = 0; i < 200; i++)
		{
			observer.Emit(ObservabilityTestSupport.Event("operation.completed", outcome: "success"));
		}
		writer.Dispose(); // bounded flush on dispose (§IO-004 best effort)

		var result = JsonlTraceReader.ReadFile(path);
		Assert.Equal(200, result.Events.Count);
		Assert.False(result.Events.Any(e => e.ProducerSequence is null));
		Assert.Empty(result.Corrupted);
	}

	[Fact]
	public void Shutdown_with_impossible_deadline_still_returns_and_admits_incompleteness()
	{
		using var temp = new TempDirectory();
		var path = temp.File("trace.jsonl");
		var (observer, queue, options) = CreateTraceOptions(path, maxFileSizeBytes: 1024 * 1024);
		options.ShutdownFlushDeadline = TimeSpan.Zero; // no time to flush

		var writer = new JsonlEventWriter(queue, observer.Identity, new FakeClock(), options);
		for (var i = 0; i < 500; i++)
		{
			observer.Emit(ObservabilityTestSupport.Event("operation.completed", outcome: "success"));
		}

		var started = DateTime.UtcNow;
		writer.Dispose();
		var elapsed = DateTime.UtcNow - started;

		Assert.True(elapsed < TimeSpan.FromSeconds(5), "dispose must honour the flush deadline, not hang");
		// Whatever remains queued was not durably written → incompleteness declared (§IO-004, no perfect-crash-durability claims).
		if (queue.Count > 0)
		{
			Assert.True(writer.ReplayIncomplete);
		}
	}

	[Fact]
	public void Collector_only_mode_has_no_file_but_consumes_the_queue_bounded()
	{
		var options = new ObservabilityOptions
		{
			Mode = ObservabilityMode.Detailed,
			Emitter = "test",
			OutputPath = null, // no sink (§IO-013: collector absent is a supported configuration)
		};
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var identity = new ObservabilityIdentity("test");
		var clock = new FakeClock();
		var observer = new CompilerObserver(options, identity, clock, queue);

		using var writer = new JsonlEventWriter(queue, identity, clock, options);
		for (var i = 0; i < 50; i++)
		{
			observer.Emit(ObservabilityTestSupport.Event("operation.completed", outcome: "success"));
		}
		writer.Flush();

		Assert.Equal(0, queue.Count);
		Assert.False(writer.IsDegraded);
		Assert.Equal(0, writer.WrittenLines); // nothing durable claimed without a sink
	}

	[Fact]
	public void Dispose_flushes_a_tail_enqueued_after_the_background_loop_has_exited()
	{
		using var temp = new TempDirectory();
		var path = temp.File("trace.jsonl");
		var (observer, queue, options) = CreateTraceOptions(path, maxFileSizeBytes: 1024 * 1024);
		options.ShutdownFlushDeadline = TimeSpan.FromSeconds(5);

		var writer = new JsonlEventWriter(queue, observer.Identity, new FakeClock(), options);

		// The real CLI emits its terminal invocation event and the session.ended event from the
		// shutdown path, at a moment when the writer's background loop has usually already drained
		// the queue and gone back to waiting (or exited entirely). Those events must still reach
		// the file on dispose; flushing only what the loop happened to pick up would silently drop
		// the tail of every trace.
		for (var i = 0; i < 10; i++)
		{
			observer.Emit(ObservabilityTestSupport.Event("operation.completed", outcome: "success"));
		}
		writer.Flush(); // simulate the loop having drained the initial burst

		observer.Emit(ObservabilityTestSupport.Event("operation.completed", outcome: "success", reason: "tail-after-loop"));
		writer.Dispose();

		var result = JsonlTraceReader.ReadFile(path);
		Assert.Equal(11, result.Events.Count);
		Assert.Contains(result.Events, e => e.Reason == "tail-after-loop");
		Assert.Empty(result.Corrupted);
	}
}
