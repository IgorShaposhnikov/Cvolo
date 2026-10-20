using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>Process identity uniqueness and atomic producer sequences under parallel load.</summary>
public class SequenceAndIdentityTests
{
	[Fact]
	public void Each_identity_gets_distinct_process_level_ids()
	{
		var a = new ObservabilityIdentity("tooling");
		var b = new ObservabilityIdentity("tooling");

		Assert.NotEqual(a.EmitterInstanceId, b.EmitterInstanceId);
		Assert.NotEqual(a.ServerInstanceId, b.ServerInstanceId);
		Assert.NotEqual(a.SessionId, b.SessionId);
		Assert.All(
			[a.EmitterInstanceId, b.EmitterInstanceId, a.ServerInstanceId, b.ServerInstanceId, a.SessionId, b.SessionId],
			id => Assert.False(string.IsNullOrWhiteSpace(id)));
	}

	[Fact]
	public void Recording_epoch_transitions_forward_only_atomically()
	{
		var identity = new ObservabilityIdentity("lsp", recordingEpoch: 1);

		Assert.Equal(1, identity.TransitionRecordingEpoch(5));
		Assert.Equal(5, identity.RecordingEpoch);
		Assert.Equal(5, identity.TransitionRecordingEpoch(3)); // backward transition refused
		Assert.Equal(5, identity.RecordingEpoch);
	}

	[Fact]
	public void Parallel_producers_get_unique_gapless_sequences()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(
			ObservabilityMode.Detailed, out var queue, queueCapacity: 100_000, clock: clock);

		const int threads = 8;
		const int perThread = 500;
		var barriers = new Barrier(threads);
		var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
		{
			barriers.SignalAndWait();
			for (var i = 0; i < perThread; i++)
			{
				observer.Emit(ObservabilityTestSupport.Event("invariant.evaluated", outcome: "success"));
			}
		})).ToArray();
		Task.WaitAll(tasks);

		var drained = queue.DrainAll();
		Assert.Equal(threads * perThread, drained.Count);

		var sequences = drained.Select(e => e.ProducerSequence).OrderBy(s => s).ToArray();
		for (var i = 0; i < sequences.Length; i++)
		{
			Assert.Equal(i + 1, sequences[i]); // atomic, starting at 1, no gaps (§58)
		}

		Assert.False(queue.Statistics.HasLoss);
	}

	[Fact]
	public void Separate_observer_instances_do_not_share_sequences_or_instance_ids()
	{
		var first = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queueA);
		var second = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queueB);

		first.Emit(ObservabilityTestSupport.Event("session.started"));
		second.Emit(ObservabilityTestSupport.Event("session.started"));

		var a = Assert.Single(queueA.DrainAll());
		var b = Assert.Single(queueB.DrainAll());
		Assert.Equal(1, a.ProducerSequence); // independent allocators
		Assert.Equal(1, b.ProducerSequence);
		Assert.NotEqual(first.Identity.EmitterInstanceId, second.Identity.EmitterInstanceId);
	}

	[Fact]
	public void Emitter_name_is_carried_in_the_envelope()
	{
		var identity = new ObservabilityIdentity("lsp");
		var clock = new FakeClock();
		var stamped = new StampedEvent(
			ObservabilityTestSupport.Event("session.started"),
			producerSequence: 1,
			timestampUtcTicks: clock.UtcNowTicks,
			monotonicTicks: clock.Timestamp,
			recordingEpoch: 2);

		var line = System.Text.Encoding.UTF8.GetString(EventJson.SerializeLine(stamped, identity, clock.ClockId));
		using var document = System.Text.Json.JsonDocument.Parse(line);
		Assert.Equal("lsp", document.RootElement.GetProperty("emitter").GetString());
		Assert.Equal(2, document.RootElement.GetProperty("recordingEpoch").GetInt32());
		Assert.Equal(1, document.RootElement.GetProperty("producerSequence").GetInt64());
	}
}
