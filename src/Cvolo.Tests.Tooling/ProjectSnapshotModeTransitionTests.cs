using System.Threading;
using Cvolo.Compiler.Tooling;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Regression coverage for snapshot correlation when the recorder is enabled
/// after a workspace/snapshot already exists (§56, OBS-AUD-002).
/// </summary>
public sealed class ProjectSnapshotModeTransitionTests
{
	private const string MainText = "int main() {\n    return 0;\n}\n";

	private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateOffObserver()
	{
		var options = new ObservabilityOptions { Mode = ObservabilityMode.Off, Emitter = "tooling-tests" };
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(
			options,
			new ObservabilityIdentity("tooling-tests"),
			new SystemObservabilityClock(),
			queue);
		return (observer, queue);
	}

	private static List<StampedEvent> DrainLazyEvents(BoundedEventQueue queue)
	{
		var events = new List<StampedEvent>();
		queue.DrainAll(events);
		return events.Where(e => e.Event.Event.StartsWith("lazy.", StringComparison.Ordinal)).ToList();
	}

	private static string? Attribute(StampedEvent evt, string key)
	{
		return evt.Event.Attributes?.FirstOrDefault(a => a.Key == key).Value;
	}

	[Fact]
	public void Snapshot_identity_has_a_public_read_only_contract_without_allocating_while_off()
	{
		var identityProperty = typeof(ProjectSnapshot).GetProperty(
			nameof(ProjectSnapshot.SnapshotId),
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
		Assert.NotNull(identityProperty);
		Assert.True(identityProperty.CanRead);
		Assert.False(identityProperty.CanWrite);
		Assert.True(identityProperty.GetMethod!.IsPublic);

		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, _) = CreateOffObserver();
		var snapshot = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath).InitialSnapshot;

		Assert.Null(identityProperty.GetValue(snapshot));
		Assert.Null(snapshot.SnapshotId);
		observer.SetMode(ObservabilityMode.Detailed);

		var actualId = Assert.IsType<string>(identityProperty.GetValue(snapshot));
		Assert.Equal(snapshot.SnapshotId, actualId);

		// The real snapshot identity survives a transition back to Off.
		observer.SetMode(ObservabilityMode.Off);
		Assert.Equal(actualId, identityProperty.GetValue(snapshot));
	}

	[Fact]
	public void Snapshot_created_off_gets_a_stable_id_and_reports_it_on_first_lazy_start()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, queue) = CreateOffObserver();
		var snapshot = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath).InitialSnapshot;

		Assert.Null(snapshot.SnapshotId);
		observer.SetMode(ObservabilityMode.Detailed);
		var id = Assert.IsType<string>(snapshot.SnapshotId);
		Assert.StartsWith("snap-", id);

		_ = snapshot.PrepareAnalysisV1();
		_ = snapshot.PrepareAnalysisV1();

		var events = DrainLazyEvents(queue);
		var started = Assert.Single(events, e => e.Event.Event == "lazy.started_by_consumer");
		var reused = Assert.Single(events, e => e.Event.Event == "lazy.reused_completed");
		Assert.Equal($"{id}:analysis", started.Event.OperationId);
		Assert.Equal(id, started.Event.SnapshotId);
		Assert.Equal(id, Attribute(started, "snapshotId"));
		Assert.Equal(id, reused.Event.SnapshotId);
		Assert.Equal(id, snapshot.SnapshotId);
	}

	[Fact]
	public void Analysis_completed_off_is_reused_with_the_late_snapshot_id_not_restarted()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, queue) = CreateOffObserver();
		var snapshot = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath).InitialSnapshot;

		_ = snapshot.PrepareAnalysisV1();
		Assert.Null(snapshot.SnapshotId);
		Assert.Empty(DrainLazyEvents(queue));

		observer.SetMode(ObservabilityMode.Detailed);
		_ = snapshot.PrepareAnalysisV1();
		var id = Assert.IsType<string>(snapshot.SnapshotId);

		var events = DrainLazyEvents(queue);
		var reused = Assert.Single(events, e => e.Event.Event == "lazy.reused_completed");
		Assert.Equal(id, reused.Event.SnapshotId);
		Assert.Equal(id, Attribute(reused, "snapshotId"));
		Assert.DoesNotContain(events, e => e.Event.Event == "lazy.started_by_consumer");
	}

	[Fact]
	public void Different_lazy_fields_in_a_preexisting_snapshot_share_one_late_identity()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, queue) = CreateOffObserver();
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = project.GetDocumentId("Main.cvl");

		observer.SetMode(ObservabilityMode.Detailed);
		_ = snapshot.PrepareAnalysisV1();
		Assert.True(snapshot.TryGetParsedUnit(document, out _));

		var id = Assert.IsType<string>(snapshot.SnapshotId);
		var starts = DrainLazyEvents(queue)
			.Where(e => e.Event.Event == "lazy.started_by_consumer")
			.ToList();
		Assert.Equal(2, starts.Count);
		Assert.Contains(starts, e => Attribute(e, "kind") == "analysis");
		Assert.Contains(starts, e => Attribute(e, "kind") == "parsed_units");
		Assert.All(starts, e => Assert.Equal(id, e.Event.SnapshotId));
		Assert.All(starts, e => Assert.Equal(id, Attribute(e, "snapshotId")));
	}

	[Fact]
	public void Two_snapshots_created_off_get_distinct_ids_when_detailed_starts()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, _) = CreateOffObserver();
		var project = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath);
		var original = project.InitialSnapshot;
		var document = project.GetDocumentId("Main.cvl");
		var derived = original.WithDocument(document, SourceText.From("int main() {\n    return 1;\n}\n"));

		Assert.Null(original.SnapshotId);
		Assert.Null(derived.SnapshotId);
		observer.SetMode(ObservabilityMode.Detailed);

		var originalId = Assert.IsType<string>(original.SnapshotId);
		var derivedId = Assert.IsType<string>(derived.SnapshotId);
		Assert.NotEqual(originalId, derivedId);

		observer.SetMode(ObservabilityMode.Off);
		Assert.Equal(originalId, original.SnapshotId);
		Assert.Equal(derivedId, derived.SnapshotId);
	}

	[Fact]
	public void Parallel_first_access_after_activation_publishes_the_same_snapshot_id()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainText));
		var (observer, _) = CreateOffObserver();
		var snapshot = CvoloWorkspace.Create(observer).OpenProject(fixture.ProjectFilePath).InitialSnapshot;
		Assert.Null(snapshot.SnapshotId);
		observer.SetMode(ObservabilityMode.Detailed);

		using var gate = new ManualResetEventSlim(false);
		var tasks = Enumerable.Range(0, 16)
			.Select(_ => Task.Run(() =>
			{
				gate.Wait();
				return snapshot.SnapshotId;
			}))
			.ToArray();
		gate.Set();
		Task.WaitAll(tasks);

		var identity = Assert.IsType<string>(tasks[0].Result);
		Assert.All(tasks, task => Assert.Equal(identity, task.Result));
	}
}
