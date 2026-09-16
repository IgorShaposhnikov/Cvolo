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
	private const int CurrentVersion = 2;

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
			var state = ReadState(node, configuration);
			var inputChanged = state is null
				|| state.Version != CurrentVersion
				|| !string.Equals(state.LocalFingerprint, node.LocalFingerprint, StringComparison.Ordinal)
				|| !string.Equals(state.BuildKey, buildKey, StringComparison.Ordinal);
			var dependencyChanged = node.ProjectReferences.Any(reference => completed[reference].RequiresBuild)
				|| (!inputChanged && !string.Equals(state!.Fingerprint, node.Fingerprint, StringComparison.Ordinal));
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

	public static void RecordSuccessful(
		ProjectBuildNode node,
		string buildKey,
		string configuration = BuildOutputLayout.DefaultConfiguration)
	{
		ArgumentNullException.ThrowIfNull(node);
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);
		WriteState(node, buildKey, configuration);
	}

	public static string GetStatePath(
		string projectDirectory,
		string configuration = BuildOutputLayout.DefaultConfiguration)
	{
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);
		return Path.Combine(projectDirectory, "obj", configuration, "cvolo.project-state.json");
	}

	private static ProjectState? ReadState(ProjectBuildNode node, string configuration)
	{
		var statePath = GetStatePath(Path.GetDirectoryName(node.ProjectPath)!, configuration);
		if (!File.Exists(statePath))
			return null;

		try
		{
			return JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(statePath), JsonOptions);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	private static void WriteState(ProjectBuildNode node, string buildKey, string configuration)
	{
		var projectDirectory = Path.GetDirectoryName(node.ProjectPath)!;
		var statePath = GetStatePath(projectDirectory, configuration);
		Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
		var state = new ProjectState(CurrentVersion, node.LocalFingerprint, node.Fingerprint, buildKey);
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
		[property: JsonPropertyName("fingerprint")] string Fingerprint,
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
