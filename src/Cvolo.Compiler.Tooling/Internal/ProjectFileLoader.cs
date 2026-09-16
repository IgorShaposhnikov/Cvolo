using System.Xml.Linq;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Minimal local-project loader: resolves a .cvlproj and its ProjectReference graph into the
/// ordered, deduplicated list of local source files, mirroring the compiler project rules
/// (recursive .cv/.cvl discovery, excluding bin/obj/.cvolo and referenced-project directories).
/// This keeps <c>Cvolo.Compiler.Tooling</c> free of any dependency on <c>Cvolo.Packaging</c>.
/// </summary>
internal static class ProjectFileLoader
{
	public static List<string> DiscoverSourceFiles(string projectFilePath)
	{
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var stack = new List<string>();
		var allFiles = new List<string>();

		Visit(Path.GetFullPath(projectFilePath));
		return [.. allFiles.Distinct(StringComparer.OrdinalIgnoreCase)];

		void Visit(string projectPath)
		{
			projectPath = Path.GetFullPath(projectPath);
			if (!visited.Add(projectPath))
				return;

			if (!active.Add(projectPath))
				throw new InvalidOperationException($"ProjectReference cycle detected involving '{projectPath}'.");

			stack.Add(projectPath);
			var doc = XDocument.Load(projectPath);
			var projectDirectory = Path.GetDirectoryName(projectPath)!;
			var references = new List<string>();

			foreach (var reference in doc.Root?.Elements("ItemGroup").Elements("ProjectReference") ?? [])
			{
				var include = ((string?)reference.Attribute("Include"))?.Trim();
				if (string.IsNullOrWhiteSpace(include))
					continue;

				var normalized = include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
				var referencePath = Path.GetFullPath(normalized, projectDirectory);
				if (!File.Exists(referencePath) || !string.Equals(Path.GetExtension(referencePath), ".cvlproj", StringComparison.OrdinalIgnoreCase))
					throw new FileNotFoundException($"ProjectReference '{include}' from '{projectPath}' was not found.", referencePath);

				references.Add(referencePath);
				Visit(referencePath);
			}

			allFiles.AddRange(EnumerateProjectSources(projectDirectory, references));
			stack.RemoveAt(stack.Count - 1);
			active.Remove(projectPath);
		}
	}

	private static IEnumerable<string> EnumerateProjectSources(string projectDirectory, IReadOnlyList<string> projectReferences)
	{
		var referenceDirectories = projectReferences
			.Select(Path.GetDirectoryName)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(path => Path.GetFullPath(path!))
			.ToArray();

		return Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories)
			.Where(path => string.Equals(Path.GetExtension(path), ".cvl", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(Path.GetExtension(path), ".cv", StringComparison.OrdinalIgnoreCase))
			.Where(path => !IsGeneratedPath(projectDirectory, path))
			.Where(path => !IsUnderProjectReference(path, referenceDirectories))
			.Select(Path.GetFullPath)
			.OrderBy(path => Normalize(Path.GetRelativePath(projectDirectory, path)), StringComparer.Ordinal);
	}

	private static bool IsUnderProjectReference(string path, IReadOnlyList<string> referenceDirectories)
	{
		var fullPath = Path.GetFullPath(path);
		foreach (var referenceDirectory in referenceDirectories)
		{
			var relative = Path.GetRelativePath(referenceDirectory, fullPath);
			if (relative.Length > 0 && relative[0] != '.' && !Path.IsPathRooted(relative))
				return true;
		}

		return false;
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
