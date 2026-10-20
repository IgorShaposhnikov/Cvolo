using System.Threading;

namespace Cvolo.Observability;

/// <summary>
/// Atomic per-emitter producer sequence (§58): assigned at event acceptance, before any
/// queueing. File order may differ from producer order across emitters; readers sort
/// per emitter instead of assuming global ordering.
/// </summary>
public sealed class SequenceAllocator
{
	private long _current;

	public SequenceAllocator(long firstSequence = 1)
	{
		_current = firstSequence - 1;
	}

	/// <summary>Returns the next strictly increasing sequence number (starts at <paramref name="firstSequence"/>).</summary>
	public long Next() => Interlocked.Increment(ref _current);

	/// <summary>Highest sequence handed out so far (0 when none).</summary>
	public long Current => Interlocked.Read(ref _current);
}
