using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>Schema compatibility: optional fields, wrong types, unknown major/minor versions.</summary>
public class SchemaCompatibilityTests
{
	[Fact]
	public void Reader_accepts_v2_events_with_optional_fields_missing()
	{
		var minimal = """{"schemaVersion":2,"event":"session.started"}""";
		var result = JsonlTraceReader.ReadText(minimal + "\n");

		var record = Assert.Single(result.Events);
		Assert.False(result.UnsupportedMajor);
		Assert.Equal("session.started", record.Event);
		Assert.Null(record.ProducerSequence); // absent fields are not fabricated (§4)
		Assert.Null(record.Outcome);
		Assert.Null(record.OperationId);
	}

	[Fact]
	public void Reader_rejects_unknown_major_schema_and_keeps_the_raw_line()
	{
		var future = """{"schemaVersion":3,"event":"operation.completed","producerSequence":7}""";
		var result = JsonlTraceReader.ReadText(future + "\n");

		Assert.True(result.UnsupportedMajor);
		var record = Assert.Single(result.Events);
		Assert.Equal(3, record.SchemaVersion);
		Assert.Contains("producerSequence", record.RawJson); // raw line kept verbatim (§57.3)
		Assert.Null(record.ProducerSequence); // interpretation refused, not guessed
		Assert.Null(record.Event); // interpretation refused, not guessed
	}

	[Fact]
	public void Reader_tolerates_newer_minor_schema_and_preserves_unknown_fields()
	{
		var line = """{"schemaVersion":2,"schemaMinor":9,"event":"session.started","futureField":{"a":1},"anotherOne":"x"}""";
		var result = JsonlTraceReader.ReadText(line + "\n");

		var record = Assert.Single(result.Events);
		Assert.False(result.UnsupportedMajor);
		Assert.Equal(9, record.SchemaMinor);
		Assert.Equal(9, result.HighestSchemaMinor); // reader records the highest minor seen in the trace
		Assert.True(record.UnknownFields.ContainsKey("futureField"));
		Assert.True(record.UnknownFields.ContainsKey("anotherOne"));
	}

	[Fact]
	public void Reader_reports_wrong_types_as_field_errors_without_failing_the_line()
	{
		var line = """{"schemaVersion":2,"event":"operation.completed","producerSequence":"not-a-number","elapsedUs":"fast","documentVersion":3}""";
		var result = JsonlTraceReader.ReadText(line + "\n");

		var record = Assert.Single(result.Events);
		Assert.Null(record.ProducerSequence);
		Assert.Null(record.ElapsedUs);
		Assert.Equal(3, record.DocumentVersion); // valid fields still read
		Assert.Contains(result.FieldErrors, e => e.Field == "producerSequence");
		Assert.Contains(result.FieldErrors, e => e.Field == "elapsedUs");
	}

	[Fact]
	public void Reader_maps_legacy_aliases_only_where_defined()
	{
		var legacy = """{"schemaVersion":2,"event":"operation.completed","sequence":5,"durationUs":120,"requestId":"lsp:9"}""";
		var result = JsonlTraceReader.ReadText(legacy + "\n");

		var record = Assert.Single(result.Events);
		Assert.True(record.UsedLegacyAlias);
		Assert.Equal(5, record.ProducerSequence); // sequence → producerSequence
		Assert.Equal(120, record.ElapsedUs); // durationUs → elapsedUs
		Assert.Null(record.RequestUid); // legacy requestId is NOT emitted/mapped (§57.1)
	}

	[Fact]
	public void Reader_maps_legacy_logging_dropped_to_canonical_drop_event()
	{
		var legacy = """{"schemaVersion":2,"event":"logging.dropped","reason":"queue_overflow"}""";
		var result = JsonlTraceReader.ReadText(legacy + "\n");

		var record = Assert.Single(result.Events);
		Assert.Equal("trace.events_dropped", record.Event);
		Assert.Equal(1, result.LegacyDropLines);
	}

	[Fact]
	public void Reader_flags_events_missing_from_the_catalog_as_unknown()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var text = """
			{"schemaVersion":2,"event":"session.started","producerSequence":1}
			{"schemaVersion":2,"event":"made_up.event","producerSequence":2}
			""";
		var result = JsonlTraceReader.ReadText(text, catalog);

		Assert.Equal(2, result.Events.Count);
		Assert.False(result.Events[0].UnknownEvent);
		Assert.True(result.Events[1].UnknownEvent);
		Assert.Equal("made_up.event", Assert.Single(result.UnknownEventNames));
	}
}
