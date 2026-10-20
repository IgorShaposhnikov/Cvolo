using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Immutable accepted event: payload plus emit-time stamps (§58 producerSequence,
/// timestamps from the emitter's own clock domain, recording epoch in force at emit).
/// </summary>
public readonly struct StampedEvent
{
	public StampedEvent(in CompilerEvent evt, long producerSequence, long timestampUtcTicks, long monotonicTicks, long recordingEpoch)
	{
		Event = evt;
		ProducerSequence = producerSequence;
		TimestampUtcTicks = timestampUtcTicks;
		MonotonicTicks = monotonicTicks;
		RecordingEpoch = recordingEpoch;
	}

	public CompilerEvent Event { get; }

	public long ProducerSequence { get; }

	public long TimestampUtcTicks { get; }

	public long MonotonicTicks { get; }

	public long RecordingEpoch { get; }
}
