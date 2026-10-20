using System.Buffers;
using System.Text.Json;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Canonical v4.1 JSONL envelope codec (§4, §57.1): schemaVersion 2, schemaMinor 1.
/// Absent fields are omitted — never filled with fabricated values. Legacy aliases
/// (<c>sequence</c>, <c>durationUs</c>) are accepted only by the reader, never written.
/// </summary>
public static class EventJson
{
	public const int SchemaVersion = 2;
	public const int SchemaMinor = 1;

	/// <summary>Serializes one stamped event as a single UTF-8 JSON line without trailing newline (§4 JSONL).</summary>
	public static byte[] SerializeLine(in StampedEvent stamped, ObservabilityIdentity identity, string clockId)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			WriteEnvelope(writer, stamped, identity, clockId);
		}
		return buffer.WrittenSpan.ToArray();
	}

	public static void WriteEnvelope(Utf8JsonWriter writer, in StampedEvent stamped, ObservabilityIdentity identity, string clockId)
	{
		var evt = stamped.Event;
		writer.WriteStartObject();
		writer.WriteNumber("schemaVersion", SchemaVersion);
		writer.WriteNumber("schemaMinor", SchemaMinor);
		writer.WriteString("emitter", identity.Emitter);
		writer.WriteString("emitterInstanceId", identity.EmitterInstanceId);
		writer.WriteString("sessionId", identity.SessionId);
		writer.WriteString("serverInstanceId", identity.ServerInstanceId);
		writer.WriteNumber("recordingEpoch", stamped.RecordingEpoch);
		writer.WriteNumber("producerSequence", stamped.ProducerSequence);
		writer.WriteString("timestampUtc", new DateTime(stamped.TimestampUtcTicks, DateTimeKind.Utc));
		writer.WriteNumber("monotonicTicks", stamped.MonotonicTicks);
		writer.WriteString("clockId", clockId);
		writer.WriteString("event", evt.Event);

		WriteStringIfPresent(writer, "stage", evt.Stage);
		WriteStringIfPresent(writer, "outcome", evt.Outcome);
		WriteStringIfPresent(writer, "reason", evt.Reason);
		if (evt.ElapsedUs is { } elapsedUs)
		{
			writer.WriteNumber("elapsedUs", elapsedUs);
		}

		WriteStringIfPresent(writer, "operationId", evt.OperationId);
		WriteStringIfPresent(writer, "parentOperationId", evt.ParentOperationId);
		WriteStringIfPresent(writer, "requestUid", evt.RequestUid);
		if (evt.RpcRequestId is { } rpcRequestId)
		{
			writer.WriteStartObject("rpcRequestId");
			writer.WriteString("type", rpcRequestId.Type);
			if (rpcRequestId.IsNumber)
			{
				// Preserve the protocol JSON type (§5): numbers stay JSON numbers.
				writer.WritePropertyName("value");
				writer.WriteRawValue(rpcRequestId.Value, skipInputValidation: false);
			}
			else
			{
				writer.WriteString("value", rpcRequestId.Value);
			}
			writer.WriteEndObject();
		}

		WriteStringIfPresent(writer, "analysisId", evt.AnalysisId);
		WriteStringIfPresent(writer, "snapshotId", evt.SnapshotId);
		WriteStringIfPresent(writer, "indexId", evt.IndexId);
		WriteStringIfPresent(writer, "generationId", evt.GenerationId);
		WriteStringIfPresent(writer, "documentSessionId", evt.DocumentSessionId);
		if (evt.DocumentVersion is { } documentVersion)
		{
			writer.WriteNumber("documentVersion", documentVersion);
		}
		WriteStringIfPresent(writer, "clientActionId", evt.ClientActionId);

		if (evt.Attributes is { Count: > 0 } attributes)
		{
			writer.WriteStartObject("attributes");
			foreach (var pair in attributes)
			{
				writer.WriteString(pair.Key, pair.Value);
			}
			writer.WriteEndObject();
		}

		writer.WriteEndObject();
	}

	/// <summary>UTF-8 line including the JSONL record terminator (§4).</summary>
	public static byte[] SerializeLineWithNewline(in StampedEvent stamped, ObservabilityIdentity identity, string clockId)
	{
		var line = SerializeLine(stamped, identity, clockId);
		var withNewline = new byte[line.Length + 1];
		line.CopyTo(withNewline, 0);
		withNewline[^1] = (byte)'\n';
		return withNewline;
	}

	private static void WriteStringIfPresent(Utf8JsonWriter writer, string name, string? value)
	{
		if (value is not null)
		{
			writer.WriteString(name, value);
		}
	}
}
