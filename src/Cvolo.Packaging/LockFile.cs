using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cvolo.Packaging;

public sealed record LockedPackage(string Resolved, string ContentHash, IReadOnlyDictionary<string, string> Dependencies);

public sealed class LockFile
{
	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public int Version { get; init; } = 1;
	public IReadOnlyList<string> Sources { get; init; } = [];
	public IReadOnlyDictionary<string, LockedPackage> Packages { get; init; } = new SortedDictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase);

	public static string GetPath(ProjectManifest manifest) => Path.Combine(manifest.ProjectDirectory, "cvolo.lock.json");

	public static LockFile Read(string path)
	{
		return JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), Options)
			?? throw new InvalidDataException("Invalid cvolo.lock.json.");
	}

	public void Write(string path)
	{
		var normalized = new LockFile
		{
			Version = Version,
			Sources = Sources.Order(StringComparer.Ordinal).ToArray(),
			Packages = Packages
				.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(
					p => p.Key,
					p => new LockedPackage(
						p.Value.Resolved,
						p.Value.ContentHash,
						p.Value.Dependencies.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase)),
					StringComparer.OrdinalIgnoreCase)
		};
		File.WriteAllText(path, JsonSerializer.Serialize(normalized, Options) + Environment.NewLine);
	}
}
