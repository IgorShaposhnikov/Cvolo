using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>Event catalog validation: unknown events/outcomes/reasons are flagged, never reinterpreted.</summary>
public class EventCatalogTests
{
	[Fact]
	public void Valid_event_passes_catalog_validation()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var evt = ObservabilityTestSupport.Event(
			"operation.completed",
			outcome: "success",
			operationId: "op-1");

		var violations = catalog.Validate(evt);

		Assert.Empty(violations);
	}

	[Fact]
	public void Unknown_event_code_is_flagged_as_unknown_event()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var evt = ObservabilityTestSupport.Event("definitely.not.registered", outcome: "success");

		var violations = catalog.Validate(evt);

		Assert.Contains(violations, v => v.StartsWith("unknown_event:"));
	}

	[Fact]
	public void Unknown_outcome_for_known_event_is_flagged_not_silently_accepted()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var evt = ObservabilityTestSupport.Event("operation.completed", outcome: "totally_fine");

		var violations = catalog.Validate(evt);

		Assert.Contains(violations, v => v.StartsWith("unknown_outcome:operation.completed:"));
	}

	[Fact]
	public void Unknown_reason_for_known_event_is_flagged()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var evt = ObservabilityTestSupport.Event(
			"operation.faulted",
			outcome: "faulted",
			reason: "made_up_reason",
			operationId: "op-1");

		var violations = catalog.Validate(evt);

		Assert.Contains(violations, v => v.StartsWith("unknown_reason:operation.faulted:"));
	}

	[Fact]
	public void Missing_required_field_is_reported()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var evt = ObservabilityTestSupport.Event("operation.completed", outcome: "success"); // no operationId

		var violations = catalog.Validate(evt);

		Assert.Contains(violations, v => v.StartsWith("missing_field:operation.completed:operationId"));
	}

	[Theory]
	[InlineData("rpc.request.received", "lsp")]
	[InlineData("rpc.response.sent", "lsp")]
	[InlineData("document.update.rejected", "core")]
	[InlineData("diagnostics.publish_sent", "lsp")]
	[InlineData("snapshot.capture.completed", "core")]
	[InlineData("feature.request.started", "lsp.feature")]
	public void Kernel_catalog_registers_LSP_and_core_events(string code, string component)
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		Assert.True(catalog.TryGet(code, out var definition), $"Missing event: {code}");
		Assert.Equal(component, definition.Component);
	}

	[Fact]
	public void Rpc_received_requires_real_uid_and_valid_outcome()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var valid = new CompilerEvent
		{
			Event = "rpc.request.received",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P1,
			Outcome = "received",
			RequestUid = "server-1/rpc/1",
			RpcRequestId = RpcRequestId.Number(1),
		};
		Assert.Empty(catalog.Validate(valid));

		var missingUid = ObservabilityTestSupport.Event("rpc.request.received", outcome: "received");
		Assert.Contains(catalog.Validate(missingUid),
			v => v == "missing_field:rpc.request.received:requestUid");
		Assert.Contains(catalog.Validate(valid with { Outcome = "success" }),
			v => v == "unknown_outcome:rpc.request.received:success");
	}

	[Fact]
	public void Rejected_document_update_requires_machine_reason()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		var missing = ObservabilityTestSupport.Event("document.update.rejected", outcome: "rejected");
		Assert.Contains(catalog.Validate(missing),
			v => v == "missing_field:document.update.rejected:reason");
		var rejected = ObservabilityTestSupport.Event(
			"document.update.rejected", outcome: "rejected", reason: "stale_version");
		Assert.Empty(catalog.Validate(rejected));
	}

	[Fact]
	public void Hot_path_counters_accumulate_the_same_classifications()
	{
		var catalog = EventCatalog.CreateKernelCatalog();
		catalog.CountViolations(ObservabilityTestSupport.Event("nope.one"));
		catalog.CountViolations(ObservabilityTestSupport.Event("nope.two"));
		catalog.CountViolations(ObservabilityTestSupport.Event(
			"operation.completed",
			outcome: "bogus",
			operationId: "op-2")); // unknown outcome only — required fields present
		catalog.CountViolations(ObservabilityTestSupport.Event("operation.completed", outcome: "success")); // invalid: missing operationId
		catalog.CountViolations(ObservabilityTestSupport.Event(
			"operation.completed",
			outcome: "success",
			operationId: "op-1"));

		Assert.Equal(2, catalog.UnknownEvents);
		Assert.Equal(1, catalog.UnknownOutcomes);
		Assert.Equal(1, catalog.MissingFields);
		Assert.Equal(0, catalog.UnknownReasons);
	}
}
