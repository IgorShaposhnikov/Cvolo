namespace Cvolo.Packaging;

/// <summary>
/// Removes generated build outputs without touching package restore/extraction state.
/// When a configuration is supplied, only that configuration's bin/obj directories
/// are removed; otherwise the complete build output roots are deleted.
/// </summary>
public static class BuildCleaner
{
	public static IReadOnlyList<string> Clean(string projectDirectory, string? configuration = null)
	{
		if (string.IsNullOrWhiteSpace(projectDirectory))
			throw new ArgumentException("Project directory cannot be empty.", nameof(projectDirectory));

		var root = Path.GetFullPath(projectDirectory);
		if (!Directory.Exists(root))
			throw new DirectoryNotFoundException($"Project directory '{root}' was not found.");

		var targets = configuration is null
			? [BuildOutputLayout.GetObjRoot(root), BuildOutputLayout.GetBinRoot(root)]
			: GetConfigurationTargets(root, configuration);

		var deleted = new List<string>(targets.Length);
		foreach (var target in targets)
		{
			if (!Directory.Exists(target))
				continue;

			Directory.Delete(target, recursive: true);
			deleted.Add(target);
		}

		return deleted;
	}

	private static string[] GetConfigurationTargets(string projectDirectory, string configuration)
	{
		var normalized = BuildOutputLayout.NormalizeConfiguration(configuration);
		return
		[
			BuildOutputLayout.GetObjDirectory(projectDirectory, normalized),
			BuildOutputLayout.GetBinDirectory(projectDirectory, normalized)
		];
	}
}
