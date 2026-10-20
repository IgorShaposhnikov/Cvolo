using System.Threading;

namespace Cvolo.Observability;

/// <summary>
/// Identity and lifecycle ids of one emitter process (§5, §57.2):
/// <c>sessionId</c> = logical client session (may be passed in from the client),
/// <c>serverInstanceId</c> = fresh id per server process launch (never a PID),
/// <c>emitterInstanceId</c> = per process/worker writer run,
/// <c>recordingEpoch</c> = bumps on every effective policy transition.
/// </summary>
public sealed class ObservabilityIdentity
{
	private long _recordingEpoch;

	public ObservabilityIdentity(
		string emitter,
		string? sessionId = null,
		string? serverInstanceId = null,
		string? emitterInstanceId = null,
		long recordingEpoch = 0)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(emitter);
		Emitter = emitter;
		SessionId = sessionId ?? Guid.NewGuid().ToString("N");
		ServerInstanceId = serverInstanceId ?? Guid.NewGuid().ToString("N");
		EmitterInstanceId = emitterInstanceId ?? Guid.NewGuid().ToString("N");
		_recordingEpoch = recordingEpoch;
	}

	/// <summary>Source component name: "tooling", "lsp", "compiler", "cli", "test"… (§4).</summary>
	public string Emitter { get; }

	public string SessionId { get; }

	public string ServerInstanceId { get; }

	public string EmitterInstanceId { get; }

	public long RecordingEpoch => Interlocked.Read(ref _recordingEpoch);

	/// <summary>
	/// Atomically moves to a new recording epoch (§57.2). Returns the previous epoch so the
	/// caller can record the transition (recording.mode_changed / recording.cutoff, §58).
	/// </summary>
	public long TransitionRecordingEpoch(long newEpoch)
	{
		long previous;
		do
		{
			previous = Interlocked.Read(ref _recordingEpoch);
			if (newEpoch <= previous)
			{
				return previous;
			}
		}
		while (Interlocked.CompareExchange(ref _recordingEpoch, newEpoch, previous) != previous);
		return previous;
	}
}
