using System.Text.Json;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Bounded JSONL event writer (§14, §58):
/// drains the queue in batches on a background loop, never blocks producers, rotates at
/// file/total size limits with retention, and marks every failure honestly —
/// <see cref="IsDegraded"/>/loss counters/​<see cref="ReplayIncomplete"/> — without ever
/// throwing into the host (§14, OBS-IO-002/003/004). Durability is best-effort only:
/// a graceful shutdown flushes within <see cref="ObservabilityOptions.ShutdownFlushDeadline"/>,
/// an abrupt kill leaves an unknown tail the reader must flag.
/// </summary>
public sealed class JsonlEventWriter : IDisposable
{
	private readonly BoundedEventQueue _queue;
	private readonly ObservabilityIdentity _identity;
	private readonly IObservabilityClock _clock;
	private readonly ObservabilityOptions _options;
	private readonly object _gate = new();
	private readonly List<StampedEvent> _batch;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly Task _loop;
	private readonly string? _path;

	private Stream? _stream;
	private long _currentFileBytes;
	private long _writtenLines;
	private long _lastDurableSequence = -1;
	private long _writerLost;
	private string? _degradedReason;
	private bool _stopped;

	/// <summary>Test-host-only seam for fault injection (§45.6): not part of the public contract.</summary>
	private readonly Func<string, Stream>? _openStreamOverride;

	public JsonlEventWriter(
		BoundedEventQueue queue,
		ObservabilityIdentity identity,
		IObservabilityClock clock,
		ObservabilityOptions options)
		: this(queue, identity, clock, options, openStream: null)
	{
	}

	/// <summary>Internal ctor allowing the test host to inject a stream opener before the file is opened (§45.6).</summary>
	internal JsonlEventWriter(
		BoundedEventQueue queue,
		ObservabilityIdentity identity,
		IObservabilityClock clock,
		ObservabilityOptions options,
		Func<string, Stream>? openStream)
	{
		ArgumentNullException.ThrowIfNull(queue);
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(clock);
		ArgumentNullException.ThrowIfNull(options);
		_queue = queue;
		_identity = identity;
		_clock = clock;
		_options = options;
		_path = options.OutputPath;
		_openStreamOverride = openStream;
		_batch = new List<StampedEvent>(Math.Max(16, options.WriteBatchSize));

		if (_path is not null)
		{
			try
			{
				OpenCurrentFile();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				MarkDegraded($"open_failed:{ex.GetType().Name}");
			}
		}

		_loop = Task.Run(BackgroundLoop);
	}

	/// <summary>Total lines durably handed to the stream (counted before rotation, §58 checkpoint).</summary>
	public long WrittenLines => Interlocked.Read(ref _writtenLines);

	/// <summary>Highest producer sequence known durably written; -1 when none (§58 flush checkpoint).</summary>
	public long LastDurableProducerSequence => Interlocked.Read(ref _lastDurableSequence);

	/// <summary>Writer-level event loss due to IO failures (§14 counters).</summary>
	public long WriterLost => Interlocked.Read(ref _writerLost);

	public bool IsDegraded { get; private set; }

	public string? DegradedReason
	{
		get
		{
			lock (_gate)
			{
				return _degradedReason;
			}
		}
	}

	/// <summary>
	/// True when the trace cannot claim completeness: queue drops, writer loss, or
	/// undrained events at shutdown (§14, §58 — never claim replay_complete otherwise).
	/// </summary>
	public bool ReplayIncomplete
	{
		get
		{
			var stats = _queue.Statistics;
			return stats.HasLoss
				|| Interlocked.Read(ref _writerLost) > 0
				|| _queue.Count > 0
				|| IsDegraded;
		}
	}

	/// <summary>Synchronous drain of everything currently queued (deterministic tests + shutdown).</summary>
	public int Flush() => FlushCore(maxRounds: int.MaxValue);

	/// <summary>Stops the background loop, flushes best-effort within the deadline, closes the file (§IO-004).</summary>
	public void Dispose()
	{
		_shutdown.Cancel();
		try
		{
			_loop.Wait(_options.ShutdownFlushDeadline);
		}
		catch (AggregateException)
		{
			// Loop fault is surfaced through degraded status below, never rethrown (§30).
		}

		lock (_gate)
		{
			if (!_stopped)
			{
				// Flush the tail before marking the writer stopped: the background loop has already
				// exited by now, so this is the last chance to hand queued events to the stream
				// within the deadline. Marking stopped first would discard them silently (§IO-004).
				if (_queue.Count > 0)
				{
					FlushCoreUnlocked(maxRounds: int.MaxValue, within: _options.ShutdownFlushDeadline);
				}
				_stopped = true;
				CloseStream();
			}
		}
		_shutdown.Dispose();
	}

	private void BackgroundLoop()
	{
		while (!_shutdown.IsCancellationRequested)
		{
			try
			{
				FlushCore(maxRounds: 8);
			}
			catch (Exception ex)
			{
				MarkDegraded($"loop:{ex.GetType().Name}");
			}

			try
			{
				_shutdown.Token.WaitHandle.WaitOne(_options.FlushInterval);
			}
			catch (ObjectDisposedException)
			{
				return;
			}
		}
	}

	private int FlushCore(int maxRounds)
	{
		lock (_gate)
		{
			return FlushCoreUnlocked(maxRounds, within: null);
		}
	}

	private int FlushCoreUnlocked(int maxRounds, TimeSpan? within)
	{
		if (_path is null)
		{
			// Collector-only mode: consume so the queue stays bounded, count as durable nothing (§IO-013).
			var consumed = 0;
			_batch.Clear();
			while (maxRounds-- > 0 && _queue.Drain(_options.WriteBatchSize, _batch) > 0)
			{
				consumed += _batch.Count;
				foreach (var evt in _batch)
				{
					Interlocked.Exchange(ref _lastDurableSequence, Math.Max(LastDurableProducerSequence, evt.ProducerSequence));
				}
				_batch.Clear();
			}
			return consumed;
		}

		if (_stopped)
		{
			return 0;
		}

		var deadline = within is { } window ? DateTime.UtcNow + window : (DateTime?)null;
		var total = 0;
		for (var round = 0; round < maxRounds; round++)
		{
			if (deadline is not null && DateTime.UtcNow > deadline)
			{
				break; // flush deadline reached: remaining events stay queued → ReplayIncomplete (§IO-004)
			}

			_batch.Clear();
			var drained = _queue.Drain(_options.WriteBatchSize, _batch);
			if (drained == 0)
			{
				break;
			}

			try
			{
				foreach (var stamped in _batch)
				{
					EnsureCapacityForNextLine();
					var line = EventJson.SerializeLineWithNewline(stamped, _identity, _clock.ClockId);
					_stream!.Write(line, 0, line.Length);
					_currentFileBytes += line.Length;
					Interlocked.Increment(ref _writtenLines);
					Interlocked.Exchange(ref _lastDurableSequence, Math.Max(LastDurableProducerSequence, stamped.ProducerSequence));
				}
				_stream!.Flush(); // buffered to the OS; durable-to-disk policy is best-effort (§IO-004)
				total += drained;
				_batch.Clear();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
			{
				// Events in this batch are lost at the writer level: count them, degrade, continue (§14).
				Interlocked.Add(ref _writerLost, _batch.Count);
				MarkDegraded($"write_failed:{ex.GetType().Name}");
				_batch.Clear();
				break;
			}
		}

		return total;
	}

	private void EnsureCapacityForNextLine()
	{
		if (_stream is null)
		{
			OpenCurrentFile();
		}
		else if (_options.MaxFileSizeBytes > 0 && _currentFileBytes >= _options.MaxFileSizeBytes)
		{
			Rotate();
		}
	}

	private void OpenCurrentFile()
	{
		var path = _path ?? throw new InvalidOperationException("no output path configured");
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		_stream = _openStreamOverride is not null
			? _openStreamOverride(path)
			: new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
		_currentFileBytes = _stream.CanSeek ? _stream.Length : 0;
		// A pre-existing file (append across restarts) starts a fresh readable tail; its
		// previous content stays valid for the reader, rotation counts it as current file.
	}

	private void Rotate()
	{
		CloseStream();

		var basePath = _path!;
		for (var index = GetHighestRotationIndex(basePath); index >= 1; index--)
		{
			var source = RotationPath(basePath, index);
			if (!File.Exists(source))
			{
				continue;
			}
			var target = RotationPath(basePath, index + 1);
			if (index + 1 > _options.MaxRotatedFiles || IsBeyondTotalBudget(target))
			{
				File.Delete(source); // retention (§IO-003)
				continue;
			}
			File.Move(source, target, overwrite: true);
		}

		if (File.Exists(basePath) && !IsBeyondTotalBudget(RotationPath(basePath, 1)) && _options.MaxRotatedFiles >= 1)
		{
			File.Move(basePath, RotationPath(basePath, 1), overwrite: true);
		}
		else if (File.Exists(basePath))
		{
			File.Delete(basePath);
		}

		OpenCurrentFile();
	}

	private bool IsBeyondTotalBudget(string candidatePath)
	{
		if (_options.MaxTotalSizeBytes <= 0)
		{
			return false;
		}
		return TotalRetainedBytes() > _options.MaxTotalSizeBytes;
	}

	private long TotalRetainedBytes()
	{
		long total = 0;
		var basePath = _path!;
		try
		{
			if (File.Exists(basePath))
			{
				total += new FileInfo(basePath).Length;
			}
			for (var index = 1; index <= Math.Max(1, _options.MaxRotatedFiles); index++)
			{
				var rotated = RotationPath(basePath, index);
				if (File.Exists(rotated))
				{
					total += new FileInfo(rotated).Length;
				}
			}
		}
		catch (IOException)
		{
			// Size accounting is best-effort; strict per-file limit still applies.
		}
		return total;
	}

	private static string RotationPath(string basePath, int index) => $"{basePath}.{index}";

	private static int GetHighestRotationIndex(string basePath)
	{
		var highest = 0;
		var directory = Path.GetDirectoryName(basePath);
		var name = Path.GetFileName(basePath);
		if (string.IsNullOrEmpty(directory))
		{
			directory = ".";
		}

		try
		{
			foreach (var file in Directory.EnumerateFiles(directory, name + ".*"))
			{
				var suffix = Path.GetExtension(file);
				if (suffix.Length > 1 && int.TryParse(suffix.AsSpan(1), out var index))
				{
					highest = Math.Max(highest, index);
				}
			}
		}
		catch (IOException)
		{
			// Enumeration failure only affects rotation start index; writes continue.
		}
		return highest;
	}

	private void CloseStream()
	{
		try
		{
			_stream?.Dispose();
		}
		catch (IOException)
		{
			// Closing failures do not change host behaviour (§30).
		}
		_stream = null;
	}

	private void MarkDegraded(string reason)
	{
		lock (_gate)
		{
			IsDegraded = true;
			_degradedReason ??= reason;
		}
	}
}
