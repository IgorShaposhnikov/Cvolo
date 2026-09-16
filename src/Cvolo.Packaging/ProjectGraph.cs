using System.Xml.Linq;

namespace Cvolo.Packaging;

/// <summary>
/// Canonical dependency-first view of a .cvlproj ProjectReference graph.
/// This is the shared graph model for compilation, incremental fingerprinting,
/// and future per-project build scheduling.
/// </summary>
public sealed class ProjectGraph
{
	private ProjectGraph(string rootProjectPath, IReadOnlyList<ProjectGraphNode> nodes)
	{
		RootProjectPath = rootProjectPath;
		ProjectDirectory = Path.GetDirectoryName(rootProjectPath)!;
		Nodes = nodes;
		Root = nodes[^1];
	}

	public string RootProjectPath { get; }
	public string ProjectDirectory { get; }
	public IReadOnlyList<ProjectGraphNode> Nodes { get; }
	public ProjectGraphNode Root { get; }

	public static bool TryLoad(string pathOrDirectory, out ProjectGraph? graph)
	{
		graph = null;
		var projectPath = LocateProject(pathOrDirectory);
		if (projectPath is null)
			return false;

		graph = LoadProject(projectPath);
		return true;
	}

	public static ProjectGraph Load(string pathOrDirectory)
	{
		var projectPath = LocateProject(pathOrDirectory)
			?? throw new FileNotFoundException($"Could not locate a .cvlproj for '{pathOrDirectory}'.");
		return LoadProject(projectPath);
	}

	private static ProjectGraph LoadProject(string rootProjectPath)
	{
		rootProjectPath = Path.GetFullPath(rootProjectPath);
		var visited = new Dictionary<string, ProjectGraphNode>(StringComparer.OrdinalIgnoreCase);
		var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var stack = new List<string>();
		var ordered = new List<ProjectGraphNode>();

		Visit(rootProjectPath);
		return new ProjectGraph(rootProjectPath, ordered);

		ProjectGraphNode Visit(string projectPath)
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
				Visit(referencePath);
			}

			var node = new ProjectGraphNode(projectPath, EnumerateProjectSources(projectDirectory).ToArray(), references);
			visited.Add(projectPath, node);
			ordered.Add(node); // dependency-first because each ProjectReference is visited first.
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

	private static string Normalize(string path) => path.Replace('\\', '/');
}

public sealed record ProjectGraphNode(
	string ProjectPath,
	IReadOnlyList<string> SourceFiles,
	IReadOnlyList<string> ProjectReferences)
{
	public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;
}
