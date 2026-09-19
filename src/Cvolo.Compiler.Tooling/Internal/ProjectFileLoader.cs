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
	public static List<string> DiscoverSourceFiles(string projectFilePath, bool includeStandardLibrary = false)
	{
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var stack = new List<string>();
		var allFiles = new List<string>();

		Visit(Path.GetFullPath(projectFilePath));

		var files = allFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		// When requested, compile the standard library alongside the project (mirroring
		// CompilationProject.Load) so standard-library APIs resolve instead of reporting false
		// 'no overload' errors. Hosts that want the raw project file set keep the default.
		if (includeStandardLibrary)
		{
			var standardLibrary = FindStandardLibraryPath(Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!);
			if (standardLibrary is not null)
			{
				foreach (var file in EnumerateLibrarySources(standardLibrary))
				{
					if (!files.Contains(file, StringComparer.OrdinalIgnoreCase))
						files.Add(file);
				}
			}
		}

		return files;

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

	/// <summary>
	/// Locates the standard-library directory the same way the compiler does: a 'libraries'
	/// folder beside the tooling assembly (shipped in the tooling bundle), else the nearest
	/// 'libraries' folder walking up from the assembly directory or the project directory.
	/// </summary>
	internal static string? FindStandardLibraryPath(string projectDirectory)
	{
		var beside = Path.Combine(AppContext.BaseDirectory, "libraries");
		if (Directory.Exists(beside))
			return beside;

		foreach (var start in new[] { AppContext.BaseDirectory, projectDirectory })
		{
			var dir = new DirectoryInfo(start);
			while (dir is not null)
			{
				var libPath = Path.Combine(dir.FullName, "libraries");
				if (Directory.Exists(libPath))
					return libPath;

				dir = dir.Parent;
			}
		}

		return null;
	}

	private static IEnumerable<string> EnumerateLibrarySources(string libraryDirectory)
	{
		return Directory.EnumerateFiles(libraryDirectory, "*.*", SearchOption.AllDirectories)
			.Where(path => string.Equals(Path.GetExtension(path), ".cvl", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(Path.GetExtension(path), ".cv", StringComparison.OrdinalIgnoreCase))
			.Select(Path.GetFullPath)
			.OrderBy(path => Normalize(path), StringComparer.Ordinal);
	}
}
