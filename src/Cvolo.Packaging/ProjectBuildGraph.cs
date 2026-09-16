using System.Security.Cryptography;
using System.Text;

namespace Cvolo.Packaging;

/// <summary>
/// Incremental-build fingerprints layered on top of the canonical ProjectReference graph.
/// Each node exposes both its own input fingerprint and a transitive fingerprint that folds
/// in referenced projects. The split lets the build planner distinguish a project that changed
/// directly from a project that only became dirty because one of its dependencies changed.
/// </summary>
public sealed class ProjectBuildGraph
{
	private ProjectBuildGraph(string rootProjectPath, IReadOnlyList<ProjectBuildNode> nodes, string fingerprint)
	{
		RootProjectPath = rootProjectPath;
		ProjectDirectory = Path.GetDirectoryName(rootProjectPath)!;
		Nodes = nodes;
		Fingerprint = fingerprint;
		Root = nodes[^1];
	}

	public string RootProjectPath { get; }
	public string ProjectDirectory { get; }
	public IReadOnlyList<ProjectBuildNode> Nodes { get; }
	public ProjectBuildNode Root { get; }
	public string Fingerprint { get; }

	public static bool TryLoad(string pathOrDirectory, out ProjectBuildGraph? graph)
	{
		graph = null;
		if (!ProjectGraph.TryLoad(pathOrDirectory, out var projectGraph) || projectGraph is null)
			return false;

		graph = FromProjectGraph(projectGraph);
		return true;
	}

	public static ProjectBuildGraph Load(string pathOrDirectory) =>
		FromProjectGraph(ProjectGraph.Load(pathOrDirectory));

	private static ProjectBuildGraph FromProjectGraph(ProjectGraph projectGraph)
	{
		var rootDirectory = projectGraph.ProjectDirectory;
		var completed = new Dictionary<string, ProjectBuildNode>(StringComparer.OrdinalIgnoreCase);
		var ordered = new List<ProjectBuildNode>(projectGraph.Nodes.Count);

		foreach (var graphNode in projectGraph.Nodes)
		{
			var dependencies = graphNode.ProjectReferences
				.Select(reference => completed[reference])
				.ToArray();
			var localFingerprint = ComputeLocalFingerprint(rootDirectory, graphNode.ProjectPath, graphNode.SourceFiles);
			var fingerprint = ComputeTransitiveFingerprint(rootDirectory, graphNode.ProjectPath, localFingerprint, dependencies);
			var node = new ProjectBuildNode(
				graphNode.ProjectPath,
				graphNode.SourceFiles,
				graphNode.ProjectReferences,
				localFingerprint,
				fingerprint);
			completed.Add(graphNode.ProjectPath, node);
			ordered.Add(node);
		}

		var root = completed[projectGraph.RootProjectPath];
		return new ProjectBuildGraph(projectGraph.RootProjectPath, ordered, root.Fingerprint);
	}

	private static string ComputeLocalFingerprint(
		string rootDirectory,
		string projectPath,
		IReadOnlyList<string> sourceFiles)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendText(hash, "cvolo-project-build-inputs-v2\n");
		AppendFile(hash, rootDirectory, projectPath);

		var lockPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "cvolo.lock.json");
		if (File.Exists(lockPath))
			AppendFile(hash, rootDirectory, lockPath);
		else
			AppendText(hash, "lock:<none>\n");

		foreach (var sourceFile in sourceFiles)
			AppendFile(hash, rootDirectory, sourceFile);

		return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	private static string ComputeTransitiveFingerprint(
		string rootDirectory,
		string projectPath,
		string localFingerprint,
		IReadOnlyList<ProjectBuildNode> dependencies)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendText(hash, "cvolo-project-build-node-v2\n");
		AppendText(hash, "project:");
		AppendText(hash, Normalize(Path.GetRelativePath(rootDirectory, projectPath)));
		AppendText(hash, "\ninputs:");
		AppendText(hash, localFingerprint);
		AppendText(hash, "\n");

		foreach (var dependency in dependencies.OrderBy(node => Normalize(Path.GetRelativePath(rootDirectory, node.ProjectPath)), StringComparer.Ordinal))
		{
			AppendText(hash, "dependency:");
			AppendText(hash, Normalize(Path.GetRelativePath(rootDirectory, dependency.ProjectPath)));
			AppendText(hash, "\n");
			AppendText(hash, dependency.Fingerprint);
			AppendText(hash, "\n");
		}

		return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	private static void AppendFile(IncrementalHash hash, string rootDirectory, string path)
	{
		AppendText(hash, "file:");
		AppendText(hash, Normalize(Path.GetRelativePath(rootDirectory, path)));
		AppendText(hash, "\n");
		var bytes = File.ReadAllBytes(path);
		hash.AppendData(bytes);
		AppendText(hash, "\n");
	}

	private static void AppendText(IncrementalHash hash, string value) =>
		hash.AppendData(Encoding.UTF8.GetBytes(value));

	private static string Normalize(string path) => path.Replace('\\', '/');
}

public sealed record ProjectBuildNode(
	string ProjectPath,
	IReadOnlyList<string> SourceFiles,
	IReadOnlyList<string> ProjectReferences,
	string LocalFingerprint,
	string Fingerprint);
