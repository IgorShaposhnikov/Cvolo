using System.Security.Cryptography;
using System.Text;

namespace Cvolo.Packaging;

/// <summary>
/// Incremental-build fingerprints layered on top of the canonical ProjectReference graph.
/// Source content, project files, lock files, and referenced-project fingerprints all
/// participate in the root fingerprint, so changes anywhere in the graph invalidate
/// the consuming project.
/// </summary>
public sealed class ProjectBuildGraph
{
	private ProjectBuildGraph(string rootProjectPath, IReadOnlyList<ProjectBuildNode> nodes, string fingerprint)
	{
		RootProjectPath = rootProjectPath;
		ProjectDirectory = Path.GetDirectoryName(rootProjectPath)!;
		Nodes = nodes;
		Fingerprint = fingerprint;
	}

	public string RootProjectPath { get; }
	public string ProjectDirectory { get; }
	public IReadOnlyList<ProjectBuildNode> Nodes { get; }
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
			var fingerprint = ComputeNodeFingerprint(rootDirectory, graphNode.ProjectPath, graphNode.SourceFiles, dependencies);
			var node = new ProjectBuildNode(graphNode.ProjectPath, graphNode.SourceFiles, graphNode.ProjectReferences, fingerprint);
			completed.Add(graphNode.ProjectPath, node);
			ordered.Add(node);
		}

		var root = completed[projectGraph.RootProjectPath];
		return new ProjectBuildGraph(projectGraph.RootProjectPath, ordered, root.Fingerprint);
	}

	private static string ComputeNodeFingerprint(
		string rootDirectory,
		string projectPath,
		IReadOnlyList<string> sourceFiles,
		IReadOnlyList<ProjectBuildNode> dependencies)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendText(hash, "cvolo-project-build-node-v1\n");
		AppendFile(hash, rootDirectory, projectPath);

		var lockPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "cvolo.lock.json");
		if (File.Exists(lockPath))
			AppendFile(hash, rootDirectory, lockPath);
		else
			AppendText(hash, "lock:<none>\n");

		foreach (var sourceFile in sourceFiles)
			AppendFile(hash, rootDirectory, sourceFile);

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
	string Fingerprint);
