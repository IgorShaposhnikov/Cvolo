using Cvolo.Compiler.Tooling;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies the tooling feature-entry-point observability slice: completion, signature help, and
/// document symbols each publish an L0 span with honest counters, completion recovery publishes an
/// explainable decision when it had to recover, disabled mode records nothing while the features
/// keep working, and every emitted event satisfies the kernel catalog.
/// </summary>
public sealed class CompletionAndSignatureInstrumentationTests
{
	private const string Source =
		"int add(int a, int b) {\n" +
		"    return a + b;\n" +
		"}\n" +
		"\n" +
		"int main() {\n" +
		"    return add(1, 2);\n" +
		"}\n";

	private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver(
		ObservabilityMode mode,
		EventCatalog? catalog = null)
	{
		var options = new ObservabilityOptions
		{
			Mode = mode,
			Emitter = "tooling-tests",
			QueueCapacity = 10_000,
		};
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(
			options,
			identity: new ObservabilityIdentity("tooling-tests"),
			clock: new SystemObservabilityClock(),
			queue: queue,
			catalog: catalog);
		return (observer, queue);
	}

	private static List<StampedEvent> Drain(BoundedEventQueue queue)
	{
		var list = new List<StampedEvent>();
		queue.DrainAll(list);
		return list;
	}

	private static string? Attribute(StampedEvent evt, string key)
	{
		if (evt.Event.Attributes is null)
			return null;
		foreach (var pair in evt.Event.Attributes)
		{
			if (pair.Key == key)
				return pair.Value;
		}
		return null;
	}

	private static int At(string source, string needle, int start = 0) =>
		source.IndexOf(needle, start, StringComparison.Ordinal);

	[Fact]
	public void Completion_publishes_span_with_candidate_count_and_parse_status()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var result = document.GetCompletions(At(Source, "add(1, 2)"));
		Assert.NotNull(result);

		var events = Drain(queue);

		var started = Assert.Single(events, e => e.Event.Event == "completion.document.started");
		var completed = Assert.Single(events, e => e.Event.Event == "completion.document.completed");
		Assert.Equal(started.Event.OperationId, completed.Event.OperationId);
		Assert.Equal("success", completed.Event.Outcome);
		Assert.Equal(
			result!.Candidates.Count.ToString(),
			Attribute(completed, "candidateCount"));

		// A well-formed document is on the semantic path: unit present, no recovery needed.
		var decision = Assert.Single(events, e => e.Event.Event == "decision.completion.recovery");
		Assert.Equal("completion.document", decision.Event.Stage);
		Assert.Equal("none", decision.Event.Outcome);
		Assert.Equal("none", decision.Event.Reason);
		Assert.Equal("unit_present", Attribute(decision, "parseStatus"));
		Assert.NotNull(Attribute(decision, "queryContext"));
	}

	[Fact]
	public void Malformed_document_completion_records_explainable_recovery_decision()
	{
		// A syntactically broken declaration: the parser recovers a unit, and completion must
		// record what it actually did rather than silently absorbing the recovery (§32).
		const string broken = "int main( {\n";
		using var fixture = TempProject.Create(("Main.cvl", broken));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		_ = document.GetCompletions(broken.Length);

		var events = Drain(queue);
		var completed = Assert.Single(events, e => e.Event.Event == "completion.document.completed");
		Assert.Equal("success", completed.Event.Outcome);

		// A syntactically broken document is still recorded honestly: the recovery decision exists
		// with an explicit reason, so a trace reader can tell recovery apart from a clean run (§32).
		var decision = Assert.Single(events, e => e.Event.Event == "decision.completion.recovery");
		Assert.Equal("unit_present", Attribute(decision, "parseStatus"));
		Assert.NotNull(decision.Event.Reason);
		Assert.NotNull(decision.Event.Outcome);
	}

	[Fact]
	public void Signature_help_publishes_span_with_signature_count()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		// Cursor inside the argument list of add(1, 2).
		var result = document.GetSignatureHelp(At(Source, "add(1, 2)") + "add(".Length);

		var events = Drain(queue);
		var completed = Assert.Single(events, e => e.Event.Event == "signature_help.document.completed");
		Assert.Equal("success", completed.Event.Outcome);
		Assert.NotNull(result);
		Assert.Equal(
			result!.Signatures.Count.ToString(),
			Attribute(completed, "signatureCount"));
	}

	[Fact]
	public void Signature_help_off_call_position_reports_not_found()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		// No call encloses this position: signature help must report an explicit not_found.
		_ = document.GetSignatureHelp(At(Source, "return a + b;"));

		var events = Drain(queue);
		var completed = Assert.Single(events, e => e.Event.Event == "signature_help.document.completed");
		Assert.Equal("not_found", completed.Event.Outcome);
	}

	[Fact]
	public void Document_symbols_publishes_span_with_symbol_count()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbols = document.GetDocumentSymbols();
		Assert.NotEmpty(symbols);

		var events = Drain(queue);
		var started = Assert.Single(events, e => e.Event.Event == "document_symbols.started");
		var completed = Assert.Single(events, e => e.Event.Event == "document_symbols.completed");
		Assert.Equal(started.Event.OperationId, completed.Event.OperationId);
		Assert.Equal("success", completed.Event.Outcome);
		Assert.Equal(
			symbols.Count.ToString(),
			Attribute(completed, "symbolCount"));
	}

	[Fact]
	public void Disabled_mode_records_nothing_while_features_still_work()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Off);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		Assert.NotNull(document.GetCompletions(At(Source, "add(1, 2)")));
		Assert.NotEmpty(document.GetDocumentSymbols());
		_ = document.GetSignatureHelp(At(Source, "add(1, 2)") + "add(".Length);

		Assert.Empty(Drain(queue));
	}

	[Fact]
	public void All_feature_events_satisfy_the_kernel_catalog()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed, catalog: EventCatalog.CreateKernelCatalog());
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		_ = document.GetCompletions(At(Source, "add(1, 2)"));
		_ = document.GetSignatureHelp(At(Source, "add(1, 2)") + "add(".Length);
		_ = document.GetDocumentSymbols();

		var events = Drain(queue);
		Assert.NotEmpty(events);

		Assert.Equal(0, observer.Catalog!.UnknownEvents);
		Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
		Assert.Equal(0, observer.Catalog!.UnknownReasons);
		Assert.Equal(0, observer.Catalog!.MissingFields);
	}
}
