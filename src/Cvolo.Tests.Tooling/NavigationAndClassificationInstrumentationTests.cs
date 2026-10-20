using Cvolo.Compiler.Tooling;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies the navigation/classification observability slice: a Go-to-Definition request is
/// explainable end to end from the trace (resolve span, decision, definition mapping), misses
/// carry an explicit reason, disabled mode costs nothing, and every emitted event satisfies
/// the kernel catalog.
/// </summary>
public sealed class NavigationAndClassificationInstrumentationTests
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
	public void GoTo_definition_emits_resolve_decision_and_definition_chain()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(At(Source, "add(1, 2)"));
		Assert.NotNull(symbol);

		var definitions = snapshot.GetDefinitions(symbol!.SymbolId);
		Assert.NotEmpty(definitions);

		var events = Drain(queue);

		// Declaration index built once for this snapshot, with honest counters.
		var indexStarted = Assert.Single(events, e => e.Event.Event == "declaration_index.started");
		var indexCompleted = Assert.Single(events, e => e.Event.Event == "declaration_index.completed");
		Assert.Equal(indexStarted.Event.OperationId, indexCompleted.Event.OperationId);
		Assert.True(long.Parse(Attribute(indexCompleted, "declarationCount")!) > 0);

		// Symbol resolution: one span, found, and one correlated decision record.
		var resolveStarted = Assert.Single(events, e => e.Event.Event == "symbols.resolve.started");
		var resolveCompleted = Assert.Single(events, e => e.Event.Event == "symbols.resolve.completed");
		Assert.Equal("found", resolveCompleted.Event.Outcome);
		Assert.Equal(resolveStarted.Event.OperationId, resolveCompleted.Event.OperationId);
		Assert.NotNull(resolveCompleted.Event.ElapsedUs);
		Assert.True(resolveCompleted.Event.ElapsedUs >= 0);

		var decision = Assert.Single(events, e => e.Event.Event == "decision.made");
		Assert.Equal("symbols.resolve", decision.Event.Stage);
		Assert.Equal("found", decision.Event.Outcome);
		Assert.Equal(Attribute(resolveStarted, "position"), Attribute(decision, "position"));

		// The binder-context lock was observed around the real resolution.
		var lockWait = Assert.Single(events, e => e.Event.Event == "lock.wait.completed");
		Assert.Equal("binder_context.lock_wait", lockWait.Event.Stage);
		Assert.Equal("acquired", lockWait.Event.Outcome);
		Assert.Contains(events, e => e.Event.Event == "lock.hold.completed" && e.Event.Outcome == "released");

		// Definition mapping for the resolved symbol.
		var definitionCompleted = Assert.Single(events, e => e.Event.Event == "navigation.definition.completed");
		Assert.Equal("found", definitionCompleted.Event.Outcome);
		Assert.Equal(
			definitions.Count.ToString(),
			Attribute(definitionCompleted, "definitionCount"));
	}

	[Fact]
	public void Unresolved_position_reports_not_found_with_explainable_reason()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		// The literal `1` inside add(1, 2) is not a symbol: the miss must be explicit, never silent.
		var symbol = document.GetSymbolAtPosition(At(Source, "add(1, 2)") + "add(".Length);
		Assert.Null(symbol);

		var events = Drain(queue);

		var resolveCompleted = Assert.Single(events, e => e.Event.Event == "symbols.resolve.completed");
		Assert.Equal("not_found", resolveCompleted.Event.Outcome);
		Assert.Equal("unresolved", Attribute(resolveCompleted, "missReason"));

		var decision = Assert.Single(events, e => e.Event.Event == "decision.made");
		Assert.Equal("not_found", decision.Event.Outcome);
		Assert.Equal("unresolved", decision.Event.Reason);
	}

	[Fact]
	public void Foreign_snapshot_definition_query_records_mapping_rejection()
	{
		using var firstFixture = TempProject.Create(("Main.cvl", Source));
		using var secondFixture = TempProject.Create(("Main.cvl", Source));

		var (firstObserver, firstQueue) = CreateObserver(ObservabilityMode.Detailed);
		var firstProject = CvoloWorkspace.Create(firstObserver).OpenProject(firstFixture.ProjectFilePath);
		var secondProject = CvoloWorkspace.Create().OpenProject(secondFixture.ProjectFilePath);

		var foreignDocument = secondProject.InitialSnapshot.GetDocument(secondProject.GetDocumentId("Main.cvl"));
		var foreignSymbol = foreignDocument.GetSymbolAtPosition(At(Source, "add(1, 2)"));
		Assert.NotNull(foreignSymbol);

		// Asking snapshot A for a symbol minted by snapshot B must refuse and say why.
		var definitions = firstProject.InitialSnapshot.GetDefinitions(foreignSymbol!.SymbolId);
		Assert.Empty(definitions);

		var events = Drain(firstQueue);
		var rejected = Assert.Single(events, e => e.Event.Event == "definition.mapping.rejected");
		Assert.Equal("foreign_snapshot", rejected.Event.Reason);
		Assert.Equal("rejected", rejected.Event.Outcome);

		var definitionCompleted = Assert.Single(events, e => e.Event.Event == "navigation.definition.completed");
		Assert.Equal("not_found", definitionCompleted.Event.Outcome);
	}

	[Fact]
	public void Disabled_mode_records_nothing_while_features_still_work()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Off);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(At(Source, "add(1, 2)"));
		Assert.NotNull(symbol);
		Assert.NotEmpty(snapshot.GetDefinitions(symbol!.SymbolId));
		Assert.NotEmpty(document.GetSemanticTokens());

		Assert.Empty(Drain(queue));
	}

	[Fact]
	public void Classification_reports_summary_counters_and_encoding_metric()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var tokens = document.GetSemanticTokens();
		Assert.NotEmpty(tokens);

		var events = Drain(queue);

		var completed = Assert.Single(events, e => e.Event.Event == "classification.document.completed");
		Assert.Equal("success", completed.Event.Outcome);

		var encoded = Assert.Single(events, e => e.Event.Event == "semantic_tokens.encoded");
		Assert.Equal("encoded", encoded.Event.Outcome);
		Assert.Equal(tokens.Count.ToString(), Attribute(encoded, "tokenCount"));
		Assert.True(long.Parse(Attribute(encoded, "candidateCount")!) > 0);

		// Completed span carries the same summary counters (§755).
		Assert.Equal(Attribute(encoded, "tokenCount"), Attribute(completed, "tokenCount"));
	}

	[Fact]
	public void All_navigation_and_classification_events_satisfy_the_kernel_catalog()
	{
		using var fixture = TempProject.Create(("Main.cvl", Source));
		var (observer, queue) = CreateObserver(ObservabilityMode.Detailed, catalog: EventCatalog.CreateKernelCatalog());
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(At(Source, "add(1, 2)"));
		Assert.NotNull(symbol);
		Assert.NotEmpty(snapshot.GetDefinitions(symbol!.SymbolId));
		using (var foreignFixture = TempProject.Create(("Main.cvl", Source)))
		{
			var foreignProject = CvoloWorkspace.Create().OpenProject(foreignFixture.ProjectFilePath);
			var foreignDoc = foreignProject.InitialSnapshot.GetDocument(foreignProject.GetDocumentId("Main.cvl"));
			var foreignSymbol = foreignDoc.GetSymbolAtPosition(At(Source, "add(1, 2)"));
			Assert.NotNull(foreignSymbol);
			_ = snapshot.GetDefinitions(foreignSymbol!.SymbolId);
		}
		_ = document.GetSemanticTokens();

		var events = Drain(queue);
		Assert.NotEmpty(events);

		// Every event the whole flow emitted — parse/bind/lazy/analysis included — must be a
		// known catalog event with a known outcome/reason and all required fields (§38).
		Assert.Equal(0, observer.Catalog!.UnknownEvents);
		Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
		Assert.Equal(0, observer.Catalog!.UnknownReasons);
		Assert.Equal(0, observer.Catalog!.MissingFields);
	}
}
