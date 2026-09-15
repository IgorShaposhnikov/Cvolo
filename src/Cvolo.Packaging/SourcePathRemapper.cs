namespace Cvolo.Packaging;

/// <summary>
/// Maps physical source paths to stable virtual paths before they enter compiler artifacts.
/// Physical paths are still used for file IO; only compiler-facing source identities are remapped.
/// </summary>
public static class SourcePathRemapper
{
	public static string Map(string physicalPath, string projectDirectory, IReadOnlyList<string>? projectReferences = null)
	{
		var fullPath = Path.GetFullPath(physicalPath);
		var normalized = Normalize(fullPath);

		var stdlibMarker = "/libraries/";
		var stdlibIndex = normalized.IndexOf(stdlibMarker, StringComparison.OrdinalIgnoreCase);
		if (stdlibIndex >= 0)
			return "/stdlib/" + normalized[(stdlibIndex + stdlibMarker.Length)..];

		if (projectReferences is not null)
		{
			foreach (var reference in projectReferences)
			{
				var referenceDirectory = Path.GetDirectoryName(Path.GetFullPath(reference));
				if (referenceDirectory is null || !IsUnderDirectory(fullPath, referenceDirectory))
					continue;

				var projectName = Path.GetFileNameWithoutExtension(reference);
				var relative = Normalize(Path.GetRelativePath(referenceDirectory, fullPath));
				return $"/project-ref/{projectName}/{relative}";
			}
		}

		var root = Path.GetFullPath(projectDirectory);
		if (IsUnderDirectory(fullPath, root))
			return "/project/" + Normalize(Path.GetRelativePath(root, fullPath));

		return "/source/" + Path.GetFileName(fullPath);
	}

	private static bool IsUnderDirectory(string filePath, string directory)
	{
		var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(filePath));
		return relative != ".."
			&& !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			&& !Path.IsPathRooted(relative);
	}

	private static string Normalize(string path) => path.Replace('\\', '/');
}
