using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cvolo.Packaging;

/// <summary>
/// Persists the last successful project build fingerprint under obj/Debug.
/// The state is intentionally single-variant: changing compiler options invalidates
/// the previous state rather than risking reuse of an output produced by another variant.
/// </summary>
public static class IncrementalBuildState
{
	private const int CurrentVersion = 1;
	private const string StateFileName = "cvolo.build-state.json";

	public static bool IsUpToDate(ProjectBuildGraph graph, string buildKey, string outputPath)
	{
		ArgumentNullException.ThrowIfNull(graph);
		if (!File.Exists(outputPath))
			return false;

		var statePath = GetStatePath(graph.ProjectDirectory);
		if (!File.Exists(statePath))
			return false;

		try
		{
			var state = JsonSerializer.Deserialize<BuildState>(File.ReadAllText(statePath), JsonOptions);
			return state is not null
				&& state.Version == CurrentVersion
				&& string.Equals(state.Fingerprint, graph.Fingerprint, StringComparison.Ordinal)
				&& string.Equals(state.BuildKey, buildKey, StringComparison.Ordinal)
				&& string.Equals(state.OutputPath, NormalizeOutputPath(graph.ProjectDirectory, outputPath), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			return false;
		}
	}

	public static void Record(ProjectBuildGraph graph, string buildKey, string outputPath)
	{
		ArgumentNullException.ThrowIfNull(graph);
		var statePath = GetStatePath(graph.ProjectDirectory);
		Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
		var state = new BuildState(
			CurrentVersion,
			graph.Fingerprint,
			buildKey,
			NormalizeOutputPath(graph.ProjectDirectory, outputPath));

		var json = JsonSerializer.Serialize(state, JsonOptions);
		var temporaryPath = statePath + ".tmp_" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporaryPath, json);
			File.Move(temporaryPath, statePath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}

	public static string GetStatePath(string projectDirectory) =>
		Path.Combine(projectDirectory, "obj", LibraryBuildPipeline.DefaultConfiguration, StateFileName);

	private static string NormalizeOutputPath(string projectDirectory, string outputPath)
	{
		var fullOutput = Path.GetFullPath(outputPath);
		var relative = Path.GetRelativePath(projectDirectory, fullOutput).Replace('\\', '/');
		return relative;
	}

	private sealed record BuildState(
		[property: JsonPropertyName("version")] int Version,
		[property: JsonPropertyName("fingerprint")] string Fingerprint,
		[property: JsonPropertyName("buildKey")] string BuildKey,
		[property: JsonPropertyName("outputPath")] string OutputPath);

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};
}
