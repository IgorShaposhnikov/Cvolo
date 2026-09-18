using System.Reflection;

namespace Cvolo.Tests.Tooling;

internal static class ArtifactPaths
{
	public static string RepoRoot { get; } = FindRepoRoot();

	public static string ArtifactVersion
	{
		get
		{
			var metadata = typeof(ArtifactPaths).Assembly
				.GetCustomAttributes<AssemblyMetadataAttribute>()
				.Single(a => a.Key == "ToolingVersion");
			return metadata.Value!;
		}
	}

	public static string ArtifactDir
	{
		get
		{
			var overrideDir = Environment.GetEnvironmentVariable("CVOLO_TOOLING_ARTIFACT_DIR");

			return string.IsNullOrWhiteSpace(overrideDir)
				? Path.Combine(RepoRoot, "artifacts", "tooling", ArtifactVersion)
				: Path.GetFullPath(overrideDir);
		}
	}

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);

		while (dir is not null)
		{
			if (File.Exists(Path.Combine(dir.FullName, "src", "Cvolo.slnx")))
				return dir.FullName;

			dir = dir.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate repository root (src/Cvolo.slnx) from test output.");
	}
}
