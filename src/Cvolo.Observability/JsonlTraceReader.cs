using System.Text.Json;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>One parsed trace line (§4/§57): raw text preserved for auditability.</summary>
public sealed class TraceEventRecord
{
	public required int LineNumber { get; init; }

	public required string RawJson { get; init; }

	public long SchemaVersion { get; init; } = EventJson.SchemaVersion;

	public long SchemaMinor { get; init; }

	public string? Emitter { get; init; }

	public string? EmitterInstanceId { get; init; }

	public string? Event { get; init; }

	public long? ProducerSequence { get; init; }

	public long? RecordingEpoch { get; init; }

	public string? TimestampUtc { get; init; }

	public long? MonotonicTicks { get; init; }

	public string? ClockId { get; init; }

	public string? Stage { get; init; }

	public string? Outcome { get; init; }

	public string? Reason { get; init; }

	public long? ElapsedUs { get; init; }

	public string? OperationId { get; init; }

	public string? ParentOperationId { get; init; }

	public string? RequestUid { get; init; }

	public RpcRequestId? RpcRequestId { get; init; }

	public string? AnalysisId { get; init; }

	public string? SnapshotId { get; init; }

	public string? DocumentSessionId { get; init; }

	public long? DocumentVersion { get; init; }

	/// <summary>Known legacy aliases resolved on read (§57.1): sequence→producerSequence, durationUs→elapsedUs.</summary>
	public bool UsedLegacyAlias { get; init; }

	/// <summary>Unrecognized fields preserved for minor-safe round-trips (§57.3).</summary>
	public Dictionary<string, JsonElement> UnknownFields { get; } = [];

	/// <summary>Event code missing from the provided catalog (§57.1: flagged, never reinterpreted).</summary>
	public bool UnknownEvent { get; set; }
}

/// <summary>A line that failed JSON parsing (crash tail, corruption, §IO-003).</summary>
public sealed record TraceCorruption(int LineNumber, string Reason, string Snippet);

/// <summary>Field that exists but has the wrong type (§OBS-CORE-002): reported, not fatal.</summary>
public sealed record TraceFieldError(int LineNumber, string Field, string ExpectedType);

/// <summary>Result of reading one JSONL trace (§26, §57.3).</summary>
public sealed class TraceReadResult
{
	public List<TraceEventRecord> Events { get; } = [];

	public List<TraceCorruption> Corrupted { get; } = [];

	public List<TraceFieldError> FieldErrors { get; } = [];

	/// <summary>Unsupported major schema seen — reader must refuse interpretation (§57.3).</summary>
	public bool UnsupportedMajor { get; set; }

	public long? HighestSchemaMinor { get; set; }

	public List<string> UnknownEventNames { get; } = [];

	/// <summary>Legacy <c>logging.dropped</c> lines seen (mapped to canonical trace.events_dropped semantics, §57.1).</summary>
	public int LegacyDropLines { get; set; }

	/// <summary>Sorts events into emitter groups, each ordered by producerSequence (file order ≠ producer order, §58).</summary>
	public ILookup<string?, TraceEventRecord> ByEmitterOrdered()
	{
		var ordered = Events
			.GroupBy(e => e.Emitter, StringComparer.Ordinal)
			.SelectMany(g => g.OrderBy(x => x.ProducerSequence ?? long.MaxValue).ThenBy(x => x.LineNumber))
			.ToList();
		return ordered.ToLookup(e => e.Emitter, StringComparer.Ordinal);
	}

	/// <summary>
	/// Producer-sequence ranges missing inside each emitter's observed min..max span
	/// (drops, corruption, torn files — §58 lossRegion honesty).
	/// </summary>
	public IReadOnlyList<(string? Emitter, long FirstMissing, long LastMissing, long Count)> SequenceGaps()
	{
		var gaps = new List<(string?, long, long, long)>();
		foreach (var group in Events.GroupBy(e => e.Emitter, StringComparer.Ordinal))
		{
			var sequences = group
				.Where(e => e.ProducerSequence is not null)
				.Select(e => e.ProducerSequence!.Value)
				.Distinct()
				.OrderBy(s => s)
				.ToList();
			if (sequences.Count < 2)
			{
				continue;
			}

			var expected = sequences[0];
			foreach (var sequence in sequences)
			{
				if (sequence > expected)
				{
					gaps.Add((group.Key, expected, sequence - 1, sequence - expected));
				}
				expected = sequence + 1;
			}
		}
		return gaps;
	}

	/// <summary>Operation ids with operation.started but no terminal (crashed session, §OBS-CORE-004).</summary>
	public IReadOnlyList<string> UnterminatedOperations()
	{
		var started = new HashSet<string>(StringComparer.Ordinal);
		var ended = new HashSet<string>(StringComparer.Ordinal);
		foreach (var evt in Events)
		{
			if (evt.OperationId is not { } operationId)
			{
				continue;
			}
			if (evt.Event is "operation.started")
			{
				started.Add(operationId);
			}
			else if (evt.Event is "operation.completed" or "operation.faulted" or "operation.cancelled")
			{
				ended.Add(operationId);
			}
		}
		started.ExceptWith(ended);
		return [.. started];
	}
}

/// <summary>
/// Tolerant JSONL trace reader (§57.3, §26): rejects unknown major schema, keeps
/// unknown minor-safe fields, flags unknown events/outcomes instead of guessing,
/// resolves documented legacy aliases, and reports corrupted tails line-by-line (§IO-003).
/// </summary>
public static class JsonlTraceReader
{
	private static readonly HashSet<string> KnownEnvelopeFields = new(StringComparer.Ordinal)
	{
		"schemaVersion", "schemaMinor", "emitter", "emitterInstanceId", "sessionId", "serverInstanceId",
		"recordingEpoch", "producerSequence", "timestampUtc", "monotonicTicks", "clockId", "event",
		"stage", "outcome", "reason", "elapsedUs", "operationId", "parentOperationId", "requestUid",
		"rpcRequestId", "analysisId", "snapshotId", "indexId", "generationId", "documentSessionId",
		"documentVersion", "clientActionId", "attributes",
		// legacy aliases handled explicitly:
		"sequence", "durationUs", "requestId", "level", "component", "processId", "documentId",
	};

	public static TraceReadResult ReadFile(string path, EventCatalog? catalog = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		using var reader = new StreamReader(path);
		return ReadLines(reader, catalog);
	}

	public static TraceReadResult ReadText(string text, EventCatalog? catalog = null)
	{
		ArgumentNullException.ThrowIfNull(text);
		using var reader = new StringReader(text);
		return ReadLines(reader, catalog);
	}

	public static TraceReadResult ReadLines(TextReader reader, EventCatalog? catalog = null)
	{
		ArgumentNullException.ThrowIfNull(reader);
		var result = new TraceReadResult();
		var lineNumber = 0;
		string? line;
		while ((line = reader.ReadLine()) is not null)
		{
			lineNumber++;
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}
			ParseLine(line, lineNumber, result, catalog);
		}
		return result;
	}

	private static void ParseLine(string line, int lineNumber, TraceReadResult result, EventCatalog? catalog)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(line);
		}
		catch (JsonException ex)
		{
			result.Corrupted.Add(new TraceCorruption(lineNumber, $"invalid_json:{ex.Message}", Snippet(line)));
			return;
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				result.Corrupted.Add(new TraceCorruption(lineNumber, "not_an_object", Snippet(line)));
				return;
			}

			// schemaVersion first: unknown major means "refuse interpretation, keep raw" (§57.3).
			long schemaVersionValue = EventJson.SchemaVersion;
			if (root.TryGetProperty("schemaVersion", out var versionElement))
			{
				var parsedVersion = ExpectInt64(versionElement, "schemaVersion", result, lineNumber);
				if (parsedVersion is { } versionValue)
				{
					schemaVersionValue = versionValue;
					if (versionValue != EventJson.SchemaVersion)
					{
						result.UnsupportedMajor = true;
						result.Events.Add(new TraceEventRecord
						{
							LineNumber = lineNumber,
							RawJson = line,
							SchemaVersion = versionValue,
						});
						return;
					}
				}
			}
			else
			{
				result.FieldErrors.Add(new TraceFieldError(lineNumber, "schemaVersion", "number(required)"));
			}

			var usedLegacy = false;

			if (TryGetInt64(root, "schemaMinor", out var schemaMinor, result, lineNumber))
			{
				result.HighestSchemaMinor = Math.Max(result.HighestSchemaMinor ?? 0, schemaMinor);
			}

			// Known fields with tolerant types.
			var emitter = GetString(root, "emitter", result, lineNumber);
			var emitterInstanceId = GetString(root, "emitterInstanceId", result, lineNumber);
			var evtName = GetString(root, "event", result, lineNumber);

			// Legacy aliases (§57.1): sequence→producerSequence, durationUs→elapsedUs.
			long? producerSequence = null;
			if (root.TryGetProperty("producerSequence", out var seqElement))
			{
				producerSequence = ExpectInt64(seqElement, "producerSequence", result, lineNumber);
			}
			else if (root.TryGetProperty("sequence", out var legacySeq))
			{
				producerSequence = ExpectInt64(legacySeq, "sequence", result, lineNumber);
				usedLegacy = producerSequence is not null;
			}

			long? elapsedUs = null;
			if (root.TryGetProperty("elapsedUs", out var elapsedElement))
			{
				elapsedUs = ExpectInt64(elapsedElement, "elapsedUs", result, lineNumber);
			}
			else if (root.TryGetProperty("durationUs", out var legacyDuration))
			{
				elapsedUs = ExpectInt64(legacyDuration, "durationUs", result, lineNumber);
				usedLegacy = true;
			}

			// requestId (legacy) is deliberately NOT mapped: v4.1 keeps rpcRequestId typed (§57.1).

			if (evtName == "logging.dropped")
			{
				evtName = "trace.events_dropped";
				result.LegacyDropLines++;
				usedLegacy = true;
			}

			var record2 = new TraceEventRecord
			{
				LineNumber = lineNumber,
				RawJson = line,
				SchemaVersion = schemaVersionValue,
				SchemaMinor = TryGetInt64(root, "schemaMinor", out var minorOut, result, lineNumber) ? minorOut : 0,
				Emitter = emitter,
				EmitterInstanceId = emitterInstanceId,
				Event = evtName,
				ProducerSequence = producerSequence,
				RecordingEpoch = GetNullableInt64(root, "recordingEpoch", result, lineNumber),
				TimestampUtc = GetString(root, "timestampUtc", result, lineNumber),
				MonotonicTicks = GetNullableInt64(root, "monotonicTicks", result, lineNumber),
				ClockId = GetString(root, "clockId", result, lineNumber),
				Stage = GetString(root, "stage", result, lineNumber),
				Outcome = GetString(root, "outcome", result, lineNumber),
				Reason = GetString(root, "reason", result, lineNumber),
				ElapsedUs = elapsedUs,
				OperationId = GetString(root, "operationId", result, lineNumber),
				ParentOperationId = GetString(root, "parentOperationId", result, lineNumber),
				RequestUid = GetString(root, "requestUid", result, lineNumber),
				RpcRequestId = ReadRpcRequestId(root, result, lineNumber),
				AnalysisId = GetString(root, "analysisId", result, lineNumber),
				SnapshotId = GetString(root, "snapshotId", result, lineNumber),
				DocumentSessionId = GetString(root, "documentSessionId", result, lineNumber),
				DocumentVersion = GetNullableInt64(root, "documentVersion", result, lineNumber),
				UsedLegacyAlias = usedLegacy,
			};

			// Preserve unknown minor-safe fields (§57.3).
			foreach (var property in root.EnumerateObject())
			{
				if (!KnownEnvelopeFields.Contains(property.Name))
				{
					record2.UnknownFields[property.Name] = property.Value.Clone();
				}
			}

			// Catalog flags (§57.1): unknown event codes are flagged, not reinterpreted.
			if (catalog is not null && record2.Event is not null && !catalog.TryGet(record2.Event, out _))
			{
				record2.UnknownEvent = true;
				if (!result.UnknownEventNames.Contains(record2.Event, StringComparer.Ordinal))
				{
					result.UnknownEventNames.Add(record2.Event);
				}
			}
			else if (catalog is null && record2.Event is not null)
			{
				// Without a catalog, unknownness is unknown — do not invent flags.
			}

			result.Events.Add(record2);
		}
	}

	private static RpcRequestId? ReadRpcRequestId(JsonElement root, TraceReadResult result, int lineNumber)
	{
		if (!root.TryGetProperty("rpcRequestId", out var element))
		{
			return null;
		}
		if (element.ValueKind != JsonValueKind.Object
			|| !element.TryGetProperty("type", out var typeElement)
			|| !element.TryGetProperty("value", out var valueElement))
		{
			result.FieldErrors.Add(new TraceFieldError(lineNumber, "rpcRequestId", "object{type,value}"));
			return null;
		}

		var type = typeElement.GetString();
		if (type is "number")
		{
			return valueElement.ValueKind switch
			{
				JsonValueKind.Number => RpcRequestId.Number(valueElement.GetRawText()),
				JsonValueKind.String => RpcRequestId.Number(valueElement.GetString()!),
				_ => ReportBadRpc(),
			};
		}
		if (type is "string" && valueElement.ValueKind == JsonValueKind.String)
		{
			return RpcRequestId.Text(valueElement.GetString()!);
		}
		result.FieldErrors.Add(new TraceFieldError(lineNumber, "rpcRequestId.value", "type-consistent"));
		return null;

		RpcRequestId? ReportBadRpc()
		{
			result.FieldErrors.Add(new TraceFieldError(lineNumber, "rpcRequestId.value", "number"));
			return null;
		}
	}

	private static bool TryGetInt64(JsonElement root, string name, out long value, TraceReadResult result, int lineNumber)
	{
		value = 0;
		if (!root.TryGetProperty(name, out var element))
		{
			return false;
		}
		var parsed = ExpectInt64(element, name, result, lineNumber);
		if (parsed is { } longValue)
		{
			value = longValue;
			return true;
		}
		return false;
	}

	private static long? GetNullableInt64(JsonElement root, string name, TraceReadResult result, int lineNumber)
		=> root.TryGetProperty(name, out var element) ? ExpectInt64(element, name, result, lineNumber) : null;

	private static long? ExpectInt64(JsonElement element, string field, TraceReadResult result, int lineNumber)
	{
		if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
		{
			return value;
		}
		result.FieldErrors.Add(new TraceFieldError(lineNumber, field, "number"));
		return null;
	}

	private static string? GetString(JsonElement root, string name, TraceReadResult result, int lineNumber)
	{
		if (!root.TryGetProperty(name, out var element))
		{
			return null;
		}
		if (element.ValueKind == JsonValueKind.String)
		{
			return element.GetString();
		}
		result.FieldErrors.Add(new TraceFieldError(lineNumber, name, "string"));
		return null;
	}

	private static string Snippet(string line)
		=> line.Length <= 120 ? line : line[..120] + "…";
}
