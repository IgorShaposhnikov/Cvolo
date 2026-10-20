using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Immutable loss record for one priority lane (§14): every dropped event is counted,
/// with the producer-sequence range of the loss so analyzers can mark a lossRegion
/// instead of silently losing continuity (§58).
/// </summary>
public readonly record struct QueueLossRegion(long DroppedCount, long FirstDroppedSequence, long LastDroppedSequence);

/// <summary>Point-in-time queue statistics (§14, §33).</summary>
public sealed record QueueStatistics(
	long Enqueued,
	long DroppedP0,
	long DroppedP1,
	long DroppedP2,
	QueueLossRegion? LossP0,
	QueueLossRegion? LossP1,
	QueueLossRegion? LossP2)
{
	public long DroppedTotal => DroppedP0 + DroppedP1 + DroppedP2;

	/// <summary>true when any event was dropped — replay cannot claim completeness (§14, §58).</summary>
	public bool HasLoss => DroppedTotal > 0;
}

/// <summary>
/// Bounded multi-producer queue with three priority lanes (§14, §25).
/// Producers never block: on overflow, verbose (P2) is dropped first, then P1; P0 is
/// only displaced by another P0's admission when no P1/P2 remains. Every drop is counted
/// with its producer-sequence range — never silent. The internal lock is held only for
/// O(1) queue operations (§59: no serialization, no ToString, no callbacks under lock).
/// </summary>
public sealed class BoundedEventQueue
{
	private readonly object _gate = new();
	private Queue<StampedEvent> _p0 = new();
	private Queue<StampedEvent> _p1 = new();
	private Queue<StampedEvent> _p2 = new();
	private readonly int _capacity;

	private long _enqueued;
	private long _droppedP0;
	private long _droppedP1;
	private long _droppedP2;
	private long? _firstDroppedP0;
	private long _lastDroppedP0;
	private long? _firstDroppedP1;
	private long _lastDroppedP1;
	private long? _firstDroppedP2;
	private long _lastDroppedP2;

	public BoundedEventQueue(int capacity)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
		_capacity = capacity;
	}

	public int Capacity => _capacity;

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _p0.Count + _p1.Count + _p2.Count;
			}
		}
	}

	/// <summary>
	/// Non-blocking accept. Returns false when an event was dropped; drops are always
	/// counted in <see cref="Statistics"/> with sequence ranges (§14, §58).
	/// </summary>
	public bool TryEnqueue(in StampedEvent evt)
	{
		lock (_gate)
		{
			if (_p0.Count + _p1.Count + _p2.Count >= _capacity)
			{
				// Drop verbose first (§25): oldest P2, then oldest P1.
				if (_p2.Count > 0)
				{
					RecordDrop(_p2.Dequeue());
				}
				else if (_p1.Count > 0)
				{
					RecordDrop(_p1.Dequeue());
				}
				else
				{
					// Everything queued is P0: drop the incoming event (P0 included) —
					// the queue must stay bounded (§14).
					RecordDrop(evt);
					return false;
				}
			}

			switch (evt.Event.Priority)
			{
				case EventPriority.P0:
					_p0.Enqueue(evt);
					break;
				case EventPriority.P1:
					_p1.Enqueue(evt);
					break;
				default:
					_p2.Enqueue(evt);
					break;
			}
			_enqueued++;
			return true;
		}
	}

	/// <summary>Removes up to <paramref name="max"/> events, highest priority first, preserving FIFO within a lane.</summary>
	public int Drain(int max, List<StampedEvent> into)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
		ArgumentNullException.ThrowIfNull(into);
		lock (_gate)
		{
			var taken = 0;
			taken += DrainLane(ref _p0, max - taken, into);
			taken += DrainLane(ref _p1, max - taken, into);
			taken += DrainLane(ref _p2, max - taken, into);
			return taken;
		}
	}

	/// <summary>Empties the queue into <paramref name="into"/> (shutdown flush, §IO-004).</summary>
	public int DrainAll(List<StampedEvent> into)
	{
		ArgumentNullException.ThrowIfNull(into);
		lock (_gate)
		{
			return DrainLane(ref _p0, int.MaxValue, into)
				+ DrainLane(ref _p1, int.MaxValue, into)
				+ DrainLane(ref _p2, int.MaxValue, into);
		}
	}

	public QueueStatistics Statistics
	{
		get
		{
			lock (_gate)
			{
				return new QueueStatistics(
					_enqueued,
					_droppedP0,
					_droppedP1,
					_droppedP2,
					MakeLoss(_droppedP0, _firstDroppedP0, _lastDroppedP0),
					MakeLoss(_droppedP1, _firstDroppedP1, _lastDroppedP1),
					MakeLoss(_droppedP2, _firstDroppedP2, _lastDroppedP2));
			}
		}
	}

	private static QueueLossRegion? MakeLoss(long count, long? first, long last)
		=> count > 0 && first is long f ? new QueueLossRegion(count, f, last) : null;

	private static int DrainLane(ref Queue<StampedEvent> lane, int max, List<StampedEvent> into)
	{
		var taken = 0;
		while (taken < max && lane.Count > 0)
		{
			into.Add(lane.Dequeue());
			taken++;
		}
		return taken;
	}

	/// <summary>Counts one dropped event under its own priority and producer sequence (§14 loss counters).</summary>
	private void RecordDrop(in StampedEvent dropped)
	{
		var sequence = dropped.ProducerSequence;
		switch (dropped.Event.Priority)
		{
			case EventPriority.P0:
				_droppedP0++;
				_firstDroppedP0 ??= sequence;
				_lastDroppedP0 = sequence;
				break;
			case EventPriority.P1:
				_droppedP1++;
				_firstDroppedP1 ??= sequence;
				_lastDroppedP1 = sequence;
				break;
			default:
				_droppedP2++;
				_firstDroppedP2 ??= sequence;
				_lastDroppedP2 = sequence;
				break;
		}
	}
}
