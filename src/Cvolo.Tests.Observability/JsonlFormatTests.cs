using System.Text.Json;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>JSONL envelope format: escaping, Unicode, exception chains, typed RPC ids.</summary>
public class JsonlFormatTests
{
	[Fact]
	public void Roundtrip_preserves_unicode_quotes_and_newlines_as_single_json_lines()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);
		var identity = observer.Identity;

		var nasty = "unicode: Hello, 世界, 🚀, ÅÆØ; quotes: \"`'; backslash: \\; newline: \n; tab: \t";
		observer.Emit(ObservabilityTestSupport.Event(
			"operation.completed",
			outcome: "success",
			reason: nasty));

		var stamped = Assert.Single(queue.DrainAll());
		var line = System.Text.Encoding.UTF8.GetString(EventJson.SerializeLine(stamped, identity, clock.ClockId));

		// One physical line: the newline inside the reason is escaped, never raw (§4 JSONL).
		Assert.DoesNotContain("\n", line);
		Assert.DoesNotContain("\r", line);

		using var document = JsonDocument.Parse(line);
		Assert.Equal(nasty, document.RootElement.GetProperty("reason").GetString());
		Assert.Equal("operation.completed", document.RootElement.GetProperty("event").GetString());
	}

	[Fact]
	public void SerializeLine_produces_parseable_json_for_every_line_with_trailing_newline_separator()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);
		for (var i = 0; i < 50; i++)
		{
			observer.Emit(ObservabilityTestSupport.Event(
				i % 2 == 0 ? "parse.document.completed" : "invariant.evaluated",
				outcome: "success",
				reason: $"line-{i}-\"quoted\"-Ω"));
		}

		var lines = new List<string>();
		foreach (var stamped in queue.DrainAll())
		{
			var bytes = EventJson.SerializeLineWithNewline(stamped, observer.Identity, clock.ClockId);
			var text = System.Text.Encoding.UTF8.GetString(bytes);
			Assert.EndsWith("\n", text);
			Assert.Equal(1, text.Count(c => c == '\n'));
			using var _ = JsonDocument.Parse(text); // each line independently valid
			lines.Add(text);
		}
		Assert.Equal(50, lines.Count);
	}

	[Fact]
	public void Exception_inner_chains_are_flattened_and_survive_json_roundtrip()
	{
		var outer = new InvalidOperationException(
			"outer: invalid token \"@\"",
			new FormatException("inner: unexpected @ token\nsecond line"));

		var attributes = ExceptionFacts.Describe(outer, includeStack: false);
		var byKey = attributes.ToDictionary(p => p.Key, p => p.Value);
		Assert.Contains("InvalidOperationException", byKey["exception.type"]);
		Assert.Contains("FormatException", byKey["exception.type"]);
		Assert.Contains("->", byKey["exception.type"]);
		Assert.Contains("outer: invalid token", byKey["exception.message"]);
		Assert.Contains("inner: unexpected @ token", byKey["exception.message"]);
		Assert.DoesNotContain(byKey, p => p.Key == "exception.stack"); // no stack unless requested

		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);
		observer.Emit(new CompilerEvent
		{
			Event = "operation.faulted",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
			Outcome = "faulted",
			Reason = "exception",
			OperationId = "op-1",
			Attributes = attributes,
		});

		var line = System.Text.Encoding.UTF8.GetString(
			EventJson.SerializeLine(Assert.Single(queue.DrainAll()), observer.Identity, clock.ClockId));
		using var document = JsonDocument.Parse(line);
		var written = document.RootElement.GetProperty("attributes");
		Assert.Contains("InvalidOperationException", written.GetProperty("exception.type").GetString());
		Assert.Contains("->", written.GetProperty("exception.type").GetString());
	}

	[Fact]
	public void Reader_reports_invalid_json_lines_as_corruption_without_failing_the_file()
	{
		var good = """{"schemaVersion":2,"schemaMinor":1,"event":"session.started","producerSequence":1}""";
		var bad = """{"schemaVersion":2,"event":"broken"""; // torn tail (crash)
		var text = good + "\n" + bad + "\n" + good.Replace("session.started", "session.ended") + "\n";

		var result = JsonlTraceReader.ReadText(text);

		Assert.Equal(2, result.Events.Count);
		var corruption = Assert.Single(result.Corrupted);
		Assert.Equal(2, corruption.LineNumber);
		Assert.StartsWith("invalid_json", corruption.Reason);
	}

	[Fact]
	public void Rpc_request_id_keeps_number_and_string_types_distinct_in_json()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);

		observer.Emit(ObservabilityTestSupport.Event("request.received") with
		{
			RpcRequestId = RpcRequestId.Number(42),
		});
		observer.Emit(ObservabilityTestSupport.Event("request.received") with
		{
			RpcRequestId = RpcRequestId.Text("42"),
		});

		var drained = queue.DrainAll();
		var numberLine = System.Text.Encoding.UTF8.GetString(EventJson.SerializeLine(drained[0], observer.Identity, clock.ClockId));
		var stringLine = System.Text.Encoding.UTF8.GetString(EventJson.SerializeLine(drained[1], observer.Identity, clock.ClockId));

		using (var numberDoc = JsonDocument.Parse(numberLine))
		{
			var rpc = numberDoc.RootElement.GetProperty("rpcRequestId");
			Assert.Equal(JsonValueKind.Number, rpc.GetProperty("value").ValueKind);
			Assert.Equal(42, rpc.GetProperty("value").GetInt32());
			Assert.Equal("number", rpc.GetProperty("type").GetString());
		}

		using (var stringDoc = JsonDocument.Parse(stringLine))
		{
			var rpc = stringDoc.RootElement.GetProperty("rpcRequestId");
			Assert.Equal(JsonValueKind.String, rpc.GetProperty("value").ValueKind);
			Assert.Equal("42", rpc.GetProperty("value").GetString());
			Assert.Equal("string", rpc.GetProperty("type").GetString());
		}
	}
}
