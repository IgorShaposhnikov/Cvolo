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
