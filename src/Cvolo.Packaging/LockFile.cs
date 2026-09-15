using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cvolo.Packaging;

public sealed record LockedPackage(string Resolved, string ContentHash, IReadOnlyDictionary<string, string> Dependencies);

public sealed class LockFile
{
	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public int Version { get; init; } = 1;
	public IReadOnlyList<string> Sources { get; init; } = [];
	public IReadOnlyDictionary<string, LockedPackage> Packages { get; init; } = new SortedDictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase);

	public static string GetPath(ProjectManifest manifest) => Path.Combine(manifest.ProjectDirectory, "cvolo.lock.json");

	public static LockFile Read(string path)
	{
		var lockFile = JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), Options)
			?? throw new InvalidDataException("Invalid cvolo.lock.json.");
		if (lockFile.Version != 1)
			throw new InvalidDataException($"Unsupported cvolo.lock.json version {lockFile.Version}.");
		if (lockFile.Sources is null)
			throw new InvalidDataException("cvolo.lock.json is missing sources.");
		if (lockFile.Packages is null)
			throw new InvalidDataException("cvolo.lock.json is missing packages.");
		foreach (var package in lockFile.Packages)
		{
			if (string.IsNullOrWhiteSpace(package.Key))
				throw new InvalidDataException("cvolo.lock.json contains a package with an empty id.");
			if (string.IsNullOrWhiteSpace(package.Value.Resolved))
				throw new InvalidDataException($"cvolo.lock.json package '{package.Key}' is missing resolved.");
			if (string.IsNullOrWhiteSpace(package.Value.ContentHash))
				throw new InvalidDataException($"cvolo.lock.json package '{package.Key}' is missing contentHash.");
			if (package.Value.Dependencies is null)
				throw new InvalidDataException($"cvolo.lock.json package '{package.Key}' is missing dependencies.");
		}

		return new LockFile
		{
			Version = lockFile.Version,
			Sources = lockFile.Sources.ToArray(),
			Packages = lockFile.Packages.ToDictionary(
				p => p.Key,
				p => new LockedPackage(
					p.Value.Resolved,
					p.Value.ContentHash,
					p.Value.Dependencies.ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase)),
				StringComparer.OrdinalIgnoreCase)
		};
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
