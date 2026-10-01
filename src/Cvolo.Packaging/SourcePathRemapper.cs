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
		{
			var relative = normalized[(stdlibIndex + stdlibMarker.Length)..];
			// Tier comes from the first module directory segment, matching SdkLibraryResolver:
			// "Base" or "Base.*" is the Base tier, "System" or "System.*" the System tier.
			// Dotted sibling modules such as System.Memory/ are therefore classified like the
			// nested System/Memory/ form instead of falling through to the System catch-all.
			var slash = relative.IndexOf('/');
			var module = slash < 0 ? relative : relative[..slash];
			if (module.Equals("Base", StringComparison.OrdinalIgnoreCase))
				return "/base/" + (slash < 0 ? "" : relative[(slash + 1)..]);
			if (module.Equals("System", StringComparison.OrdinalIgnoreCase))
				return "/system/" + (slash < 0 ? "" : relative[(slash + 1)..]);
			if (module.StartsWith("Base.", StringComparison.OrdinalIgnoreCase))
				return "/base/" + module["Base.".Length..] + (slash < 0 ? "" : relative[slash..]);
			if (module.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
				return "/system/" + module["System.".Length..] + (slash < 0 ? "" : relative[slash..]);
			return "/system/" + relative;
		}

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
