using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Cvolo.Packaging;

/// <summary>
/// Deterministic view of a .cvlproj ProjectReference graph used by incremental builds.
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
		var projectPath = LocateProject(pathOrDirectory);
		if (projectPath is null)
			return false;

		graph = LoadProject(projectPath);
		return true;
	}

	public static ProjectBuildGraph Load(string pathOrDirectory)
	{
		var projectPath = LocateProject(pathOrDirectory)
			?? throw new FileNotFoundException($"Could not locate a .cvlproj for '{pathOrDirectory}'.");
		return LoadProject(projectPath);
	}

	private static ProjectBuildGraph LoadProject(string rootProjectPath)
	{
		rootProjectPath = Path.GetFullPath(rootProjectPath);
		var rootDirectory = Path.GetDirectoryName(rootProjectPath)!;
		var visited = new Dictionary<string, ProjectBuildNode>(StringComparer.OrdinalIgnoreCase);
		var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var stack = new List<string>();
		var ordered = new List<ProjectBuildNode>();

		var root = Visit(rootProjectPath);
		return new ProjectBuildGraph(rootProjectPath, ordered, root.Fingerprint);

		ProjectBuildNode Visit(string projectPath)
		{
			projectPath = Path.GetFullPath(projectPath);
			if (visited.TryGetValue(projectPath, out var existing))
				return existing;

			if (!active.Add(projectPath))
			{
				var cycleStart = stack.FindIndex(path => string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase));
				var cycle = stack.Skip(Math.Max(0, cycleStart)).Append(projectPath).Select(Path.GetFileNameWithoutExtension);
				throw new InvalidOperationException($"ProjectReference cycle detected: {string.Join(" -> ", cycle)}");
			}

			stack.Add(projectPath);
			var doc = XDocument.Load(projectPath);
			var projectDirectory = Path.GetDirectoryName(projectPath)!;
			var references = new List<string>();
			var dependencies = new List<ProjectBuildNode>();

			foreach (var reference in doc.Root?.Elements("ItemGroup").Elements("ProjectReference") ?? [])
			{
				var include = ((string?)reference.Attribute("Include"))?.Trim();
				if (string.IsNullOrWhiteSpace(include))
					continue;

				var normalizedInclude = include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
				var referencePath = Path.GetFullPath(normalizedInclude, projectDirectory);
				if (!File.Exists(referencePath) || !string.Equals(Path.GetExtension(referencePath), ".cvlproj", StringComparison.OrdinalIgnoreCase))
					throw new FileNotFoundException($"ProjectReference '{include}' from '{projectPath}' was not found.", referencePath);

				references.Add(referencePath);
				dependencies.Add(Visit(referencePath));
			}

			var sourceFiles = EnumerateProjectSources(projectDirectory).ToArray();
			var fingerprint = ComputeNodeFingerprint(rootDirectory, projectPath, sourceFiles, dependencies);
			var node = new ProjectBuildNode(projectPath, sourceFiles, references, fingerprint);
			visited.Add(projectPath, node);
			ordered.Add(node); // dependency-first because Visit(reference) completes first.
			stack.RemoveAt(stack.Count - 1);
			active.Remove(projectPath);
			return node;
		}
	}

	private static string? LocateProject(string pathOrDirectory)
	{
		if (string.IsNullOrWhiteSpace(pathOrDirectory))
			return null;

		var fullPath = Path.GetFullPath(pathOrDirectory);
		if (File.Exists(fullPath))
			return string.Equals(Path.GetExtension(fullPath), ".cvlproj", StringComparison.OrdinalIgnoreCase) ? fullPath : null;
		if (!Directory.Exists(fullPath))
			return null;

		var candidates = Directory.GetFiles(fullPath, "*.cvlproj", SearchOption.TopDirectoryOnly);
		if (candidates.Length > 1)
			throw new InvalidOperationException($"Multiple .cvlproj files found in '{pathOrDirectory}'. Pass the project file explicitly.");
		return candidates.SingleOrDefault();
	}

	private static IEnumerable<string> EnumerateProjectSources(string projectDirectory)
	{
		return Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories)
			.Where(path => string.Equals(Path.GetExtension(path), ".cvl", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(Path.GetExtension(path), ".cv", StringComparison.OrdinalIgnoreCase))
			.Where(path => !IsGeneratedPath(projectDirectory, path))
			.Select(Path.GetFullPath)
			.OrderBy(path => Normalize(Path.GetRelativePath(projectDirectory, path)), StringComparer.Ordinal);
	}

	private static bool IsGeneratedPath(string projectDirectory, string path)
	{
		var relative = Normalize(Path.GetRelativePath(projectDirectory, path));
		return relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
			|| relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
			|| relative.StartsWith(".cvolo/", StringComparison.OrdinalIgnoreCase);
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
