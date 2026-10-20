using Cvolo.Compiler.Tooling;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Compiler/Tooling instrumentation: parse.* and bind.pass.* spans, honest degraded/faulted
/// outcomes, identical functional results with telemetry on and off (§38, §12, OBS-COMP-002/003).
/// </summary>
public sealed class AnalysisInstrumentationTests
{
	private const string MainText = "int main() {\n    return 0;\n}\n";
	private const string BrokenText = "int main( {\n";

	private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver(ObservabilityMode mode)
	{
		var options = new ObservabilityOptions { Mode = mode, Emitter = "tooling-tests" };
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(options, new ObservabilityIdentity("tooling-tests"), new SystemObservabilityClock(), queue);
		return (observer, queue);
	}

	private static List<StampedEvent> Drain(BoundedEventQueue queue)
	{
		var events = new List<StampedEvent>();
		queue.DrainAll(events);
		return events;
	}

	private static string? Attribute(StampedEvent evt, string key)
	{
		if (evt.Event.Attributes is null)
		{
			return null;
		}

		foreach (var pair in evt.Event.Attributes)
		{
			if (pair.Key == key)
			{
				return pair.Value;
			}
		}

		return null;
	}

	[Fact]
	public void Analysis_reports_parse_and_bind_pass_spans_in_pass_order()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);

		_ = project.InitialSnapshot.PrepareAnalysisV1();

		var events = Drain(queue);

		// Parse: one project span + one document span inside it.
		var parseProjectStarted = events.Single(e => e.Event.Event == "parse.project.started");
		var parseProjectCompleted = events.Single(e => e.Event.Event == "parse.project.completed");
		Assert.Equal("success", parseProjectCompleted.Event.Outcome);
		Assert.Equal(parseProjectStarted.Event.OperationId, parseProjectCompleted.Event.OperationId);

		// One document span per snapshot document (fixture file + standard-library sources).
		var documentCount = project.InitialSnapshot.Documents.Count;
		Assert.True(documentCount > 1, "universe must include more than the fixture file");

		var parseDocumentStarted = events
			.Where(e => e.Event.Event == "parse.document.started")
			.ToList();
		var parseDocumentCompleted = events
			.Where(e => e.Event.Event == "parse.document.completed")
			.ToList();
		Assert.Equal(documentCount, parseDocumentStarted.Count);
		Assert.Equal(documentCount, parseDocumentCompleted.Count);
		Assert.All(parseDocumentCompleted, e => Assert.Equal("success", e.Event.Outcome));
		Assert.All(parseDocumentCompleted, e => Assert.Equal("0", Attribute(e, "errorCount")));
		Assert.All(
			parseDocumentStarted,
			e => Assert.Equal(parseProjectStarted.Event.OperationId, e.Event.ParentOperationId));
		Assert.Equal(
			documentCount.ToString(),
			Attribute(parseProjectCompleted, "documentCount"));

		// Bind: one root span for the project, labeled with its snapshot.
		var bindStarted = events.Single(e =>
			e.Event.Event == "operation.started" && e.Event.Stage == "bind.project");
		Assert.Equal($"{project.InitialSnapshot.SnapshotId}:bind", bindStarted.Event.OperationId);
		Assert.Equal(project.InitialSnapshot.SnapshotId, bindStarted.Event.AnalysisId);

		var bindCompleted = events.Single(e =>
			e.Event.Event == "operation.completed" && e.Event.Stage == "bind.project");
		Assert.Equal("success", bindCompleted.Event.Outcome);
		Assert.Equal(bindStarted.Event.OperationId, bindCompleted.Event.OperationId);

		// Every binder pass is its own span, in compiler pass order, parented to bind.project.
		var expectedPasses = new[]
		{
			"bind.pass.declaration",
			"bind.pass.validation",
			"bind.pass.flow",
			"bind.pass.safety",
			"bind.pass.ownership",
		};

		var passStarted = events
			.Where(e => e.Event.Event == "bind.pass.started")
			.ToList();
		Assert.Equal(expectedPasses, passStarted.Select(e => e.Event.Stage).ToArray());

		var passCompleted = events
			.Where(e => e.Event.Event == "bind.pass.completed")
			.ToList();
		Assert.Equal(expectedPasses, passCompleted.Select(e => e.Event.Stage).ToArray());
		Assert.All(passCompleted, e =>
		{
			Assert.Equal("success", e.Event.Outcome);
			Assert.Equal(bindStarted.Event.OperationId, e.Event.ParentOperationId);
			Assert.NotNull(e.Event.ElapsedUs);
			Assert.True(e.Event.ElapsedUs >= 0);
		});

		// Started and completed spans pair one-to-one by operation id.
		var startedIds = passStarted.Select(e => e.Event.OperationId).OrderBy(id => id, StringComparer.Ordinal);
		var completedIds = passCompleted.Select(e => e.Event.OperationId).OrderBy(id => id, StringComparer.Ordinal);
		Assert.Equal(startedIds, completedIds);
	}

	[Fact]
	public void Analysis_results_are_identical_with_and_without_instrumentation()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));

		var baselineProject = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var baseline = baselineProject.InitialSnapshot.GetAnalysis().ResultDiagnostics
			.Select(d => (d.Id, d.Severity, d.Location.Span.Start, d.Location.Span.Length))
			.ToList();

		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var observedProject = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var observed = observedProject.InitialSnapshot.GetAnalysis().ResultDiagnostics
			.Select(d => (d.Id, d.Severity, d.Location.Span.Start, d.Location.Span.Length))
			.ToList();

		Assert.Equal(baseline, observed); // diagnostics oracle is independent of telemetry
		Assert.NotEmpty(Drain(queue)); // instrumentation really ran in the observed variant
	}

	[Fact]
	public void Disabled_mode_records_no_analysis_events_and_same_diagnostics()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));

		var baselineProject = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var baseline = baselineProject.InitialSnapshot.GetAnalysis().ResultDiagnostics.Count;

		var (observer, queue) = CreateObserver(ObservabilityMode.Off);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		_ = project.InitialSnapshot.PrepareAnalysisV1();

		Assert.Equal(baseline, project.InitialSnapshot.GetAnalysis().ResultDiagnostics.Count);
		Assert.Empty(Drain(queue)); // zero events, zero cost (§30)
	}

	[Fact]
	public void Parse_errors_yield_failed_document_outcome_and_honest_bind_terminal()
	{
		using var fixture = TempProject.Create(("Main.cvl", BrokenText));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);

		_ = project.InitialSnapshot.PrepareAnalysisV1();

		var events = Drain(queue);

		var parseDocuments = events
			.Where(e => e.Event.Event == "parse.document.completed")
			.ToList();
		Assert.Equal(project.InitialSnapshot.Documents.Count, parseDocuments.Count);

		// The broken fixture document fails; every other universe document still parses.
		var failed = parseDocuments.Where(e => e.Event.Outcome == "failed").ToList();
		Assert.NotEmpty(failed);
		Assert.All(failed, e => Assert.True(int.Parse(Attribute(e, "errorCount")!) > 0));
		Assert.All(
			parseDocuments.Where(e => e.Event.Outcome == "success"),
			e => Assert.Equal("0", Attribute(e, "errorCount")));

		// The bind span still closes honestly: degraded (fallback kept original diagnostics)
		// or success (binder recovered) — never faulted/incomplete/silent-success masking.
		var bindCompleted = events.Single(e =>
			e.Event.Event == "operation.completed" && e.Event.Stage == "bind.project");
		Assert.Contains(bindCompleted.Event.Outcome, new[] { "success", "degraded" });
		Assert.Empty(events.Where(e => e.Event.Event == "analysis.faulted"));
	}

	[Fact]
	public void Second_analysis_of_identical_text_reuses_parse_cache_and_still_reports_spans()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var workspace = CvoloWorkspace.Create(observer);

		var first = workspace.OpenProject(fixture.ProjectFilePath);
		_ = first.InitialSnapshot.PrepareAnalysisV1();
		var documentCount = first.InitialSnapshot.Documents.Count;
		var firstCount = Drain(queue).Count(e => e.Event.Event == "parse.document.completed");
		Assert.Equal(documentCount, firstCount); // whole universe parsed once

		// Same file path + same text: the process-wide parse cache answers the second snapshot.
		var second = workspace.OpenProject(fixture.ProjectFilePath);
		_ = second.InitialSnapshot.PrepareAnalysisV1();

		var secondEvents = Drain(queue);
		var secondParse = secondEvents
			.Where(e => e.Event.Event == "parse.document.completed")
			.ToList();
		Assert.Equal(documentCount, secondParse.Count);
		Assert.All(secondParse, e => Assert.Equal("success", e.Event.Outcome)); // cache hit is invisible in outcome (§OBS-COMP-006)
	}
}
