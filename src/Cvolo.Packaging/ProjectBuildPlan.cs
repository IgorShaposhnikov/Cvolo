using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cvolo.Packaging;

/// <summary>
/// Computes per-project invalidation for a dependency-first ProjectBuildGraph.
/// A node is dirty when its own inputs changed, the compiler build key changed,
/// or any referenced project is dirty. Successful root builds record every node
/// so future scheduling can identify the smallest dirty subgraph.
/// </summary>
public sealed class ProjectBuildPlan
{
	private const int CurrentVersion = 1;

	private ProjectBuildPlan(ProjectBuildGraph graph, IReadOnlyList<ProjectBuildPlanNode> nodes)
	{
		Graph = graph;
		Nodes = nodes;
		Root = nodes[^1];
	}

	public ProjectBuildGraph Graph { get; }
	public IReadOnlyList<ProjectBuildPlanNode> Nodes { get; }
	public ProjectBuildPlanNode Root { get; }
	public IReadOnlyList<ProjectBuildPlanNode> DirtyNodes => Nodes.Where(node => node.RequiresBuild).ToArray();

	public static ProjectBuildPlan Create(
		ProjectBuildGraph graph,
		string buildKey,
		string configuration = BuildOutputLayout.DefaultConfiguration)
	{
		ArgumentNullException.ThrowIfNull(graph);
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);

		var completed = new Dictionary<string, ProjectBuildPlanNode>(StringComparer.OrdinalIgnoreCase);
		var nodes = new List<ProjectBuildPlanNode>(graph.Nodes.Count);
		foreach (var node in graph.Nodes)
		{
			var dependencyChanged = node.ProjectReferences.Any(reference => completed[reference].RequiresBuild);
			var inputChanged = !MatchesRecordedState(node, buildKey, configuration);
			var planNode = new ProjectBuildPlanNode(node, inputChanged, dependencyChanged);
			completed.Add(node.ProjectPath, planNode);
			nodes.Add(planNode);
		}

		return new ProjectBuildPlan(graph, nodes);
	}

	public static void RecordSuccessful(
		ProjectBuildGraph graph,
		string buildKey,
		string configuration = BuildOutputLayout.DefaultConfiguration)
	{
		ArgumentNullException.ThrowIfNull(graph);
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);

		foreach (var node in graph.Nodes)
			WriteState(node, buildKey, configuration);
	}

	public static string GetStatePath(
		string projectDirectory,
		string configuration = BuildOutputLayout.DefaultConfiguration)
	{
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);
		return Path.Combine(projectDirectory, "obj", configuration, "cvolo.project-state.json");
	}

	private static bool MatchesRecordedState(ProjectBuildNode node, string buildKey, string configuration)
	{
		var statePath = GetStatePath(Path.GetDirectoryName(node.ProjectPath)!, configuration);
		if (!File.Exists(statePath))
			return false;

		try
		{
			var state = JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(statePath), JsonOptions);
			return state is not null
				&& state.Version == CurrentVersion
				&& string.Equals(state.LocalFingerprint, node.LocalFingerprint, StringComparison.Ordinal)
				&& string.Equals(state.BuildKey, buildKey, StringComparison.Ordinal);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			return false;
		}
	}

	private static void WriteState(ProjectBuildNode node, string buildKey, string configuration)
	{
		var projectDirectory = Path.GetDirectoryName(node.ProjectPath)!;
		var statePath = GetStatePath(projectDirectory, configuration);
		Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
		var state = new ProjectState(CurrentVersion, node.LocalFingerprint, buildKey);
		var json = JsonSerializer.Serialize(state, JsonOptions);
		var temporaryPath = statePath + ".tmp_" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporaryPath, json);
			File.Move(temporaryPath, statePath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}

	private sealed record ProjectState(
		[property: JsonPropertyName("version")] int Version,
		[property: JsonPropertyName("localFingerprint")] string LocalFingerprint,
		[property: JsonPropertyName("buildKey")] string BuildKey);

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};
}

public sealed record ProjectBuildPlanNode(
	ProjectBuildNode Project,
	bool InputsChanged,
	bool DependencyChanged)
{
	public bool RequiresBuild => InputsChanged || DependencyChanged;
}
