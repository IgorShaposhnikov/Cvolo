using System.Text;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

public sealed class EventPrivacyFilterTests
{
    [Theory]
    [InlineData(ObservabilityMode.Basic)]
    [InlineData(ObservabilityMode.Detailed)]
    public void Metadata_recording_drops_sensitive_attributes_before_queue_and_jsonl(
        ObservabilityMode mode)
    {
        var observer = ObservabilityTestSupport.CreateObserver(mode, out var queue);
        var attributes = new List<KeyValuePair<string, string>>
        {
            new("method", "textDocument/definition"),
            new("exception.type", "InvalidOperationException"),
            new("exception.message", "Bearer topSecretCredential"),
            new("sourceText", "private source"),
            new("fileUri", "file:///home/user/private.cvl"),
            new("accidental", @"C:\Users\person\secret.cvl"),
            new("accessToken", "private-token"),
        };

        observer.Emit(new CompilerEvent
        {
            Event = "operation.faulted",
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Outcome = "faulted",
            OperationId = "op-1",
            Attributes = attributes,
        });

        attributes.Add(new("lateMutation", "file:///tmp/late-secret.cvl"));
        var recorded = Assert.Single(queue.DrainAll());
        var stored = recorded.Event.Attributes!.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("textDocument/definition", stored["method"]);
        Assert.Equal("InvalidOperationException", stored["exception.type"]);
        Assert.Equal("5", stored["redaction.omittedAttributeCount"]);
        Assert.DoesNotContain(stored, p => p.Key is "exception.message" or "fileUri" or "sourceText");
        Assert.DoesNotContain(stored, p => p.Key == "lateMutation");

        var jsonl = Encoding.UTF8.GetString(EventJson.SerializeLine(
            recorded, observer.Identity, "clock-fixture"));
        Assert.DoesNotContain("topSecretCredential", jsonl);
        Assert.DoesNotContain("private source", jsonl);
        Assert.DoesNotContain("private.cvl", jsonl);
        Assert.DoesNotContain("late-secret.cvl", jsonl);
    }

    [Fact]
    public void Lifecycle_emission_is_sanitized_even_when_recording_mode_is_off()
    {
        var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Off, out var queue);
        observer.EmitLifecycle(new CompilerEvent
        {
            Event = "session.ended",
            Kind = CompilerEventKind.Lifecycle,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P0,
            Attributes = [new("exception.stack", "/home/alice/private.cvl")],
        });

        var recorded = Assert.Single(queue.DrainAll());
        var attrs = recorded.Event.Attributes!.ToDictionary(p => p.Key, p => p.Value);
        Assert.Single(attrs);
        Assert.Equal("1", attrs["redaction.omittedAttributeCount"]);
    }

    [Theory]
    [InlineData(ObservabilityMode.Basic)]
    [InlineData(ObservabilityMode.Detailed)]
    public void Scalar_reason_and_string_rpc_id_are_redacted_before_queue(
        ObservabilityMode mode)
    {
        var observer = ObservabilityTestSupport.CreateObserver(mode, out var queue);
        observer.Emit(new CompilerEvent
        {
            Event = "rpc.response.sent",
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Outcome = "faulted",
            Reason = @"C:\Users\person\private.cvl",
            RpcRequestId = RpcRequestId.Text("file:///home/user/secret.cvl"),
            RequestUid = "abc/rpc/5",
        });

        var recorded = Assert.Single(queue.DrainAll());
        Assert.Equal("redacted_evidence", recorded.Event.Reason);
        Assert.Equal("string", recorded.Event.RpcRequestId!.Value.Type);
        Assert.Equal("[redacted]", recorded.Event.RpcRequestId!.Value.Value);
        Assert.Equal("abc/rpc/5", recorded.Event.RequestUid);

        var jsonl = Encoding.UTF8.GetString(
            EventJson.SerializeLine(recorded, observer.Identity, "clock-test"));
        Assert.DoesNotContain("private.cvl", jsonl);
        Assert.DoesNotContain("secret.cvl", jsonl);
    }

    [Fact]
    public void Safe_machine_reason_and_numeric_rpc_id_keep_protocol_type()
    {
        var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Basic, out var queue);
        observer.Emit(new CompilerEvent
        {
            Event = "rpc.response.sent",
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Reason = "symbol_not_found",
            RpcRequestId = RpcRequestId.Number(42),
        });
        var evt = Assert.Single(queue.DrainAll()).Event;
        Assert.Equal("symbol_not_found", evt.Reason);
        Assert.True(evt.RpcRequestId!.Value.IsNumber);
        Assert.Equal("42", evt.RpcRequestId.Value.Value);
    }

    [Fact]
    public void Metadata_only_sanitizer_preserves_bounded_machine_attributes()
    {
        var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue);
        observer.Emit(new CompilerEvent
        {
            Event = "rpc.request.received",
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Attributes = [new("method", "textDocument/definition"),
                          new("category", "definition"),
                          new("resultCount", "3")],
        });
        var stored = Assert.Single(queue.DrainAll()).Event.Attributes!.ToDictionary(
            pair => pair.Key, pair => pair.Value);
        Assert.Equal(3, stored.Count);
        Assert.Equal("textDocument/definition", stored["method"]);
    }
}
