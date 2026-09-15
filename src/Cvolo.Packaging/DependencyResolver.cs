namespace Cvolo.Packaging;

public sealed record ResolvedPackage(string Id, string Version, string ContentHash, string Path, IReadOnlyList<PackageReference> Dependencies);
public sealed record ResolvedGraph(IReadOnlyList<string> Sources, IReadOnlyDictionary<string, ResolvedPackage> Packages);

public sealed class DependencyResolver
{
	public ResolvedGraph Resolve(ProjectManifest manifest, IEnumerable<LocalFeed> feeds)
	{
		var feedList = feeds.ToList();
		var resolved = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
		var requirements = new Dictionary<string, List<(string Parent, string Range)>>(StringComparer.OrdinalIgnoreCase);
		var queue = new Queue<(string Id, string Range, string Parent)>();

		foreach (var dependency in manifest.Dependencies)
			queue.Enqueue((dependency.Id, dependency.Version, manifest.PackageId));

		while (queue.Count > 0)
		{
			var item = queue.Dequeue();
			if (!requirements.TryGetValue(item.Id, out var ranges))
			{
				ranges = [];
				requirements[item.Id] = ranges;
			}

			ranges.Add((item.Parent, item.Range));

			if (resolved.TryGetValue(item.Id, out var existing))
			{
				if (!VersionRange.Parse(item.Range).Allows(SemanticVersion.Parse(existing.Version)))
					ThrowConflict(item.Id, ranges);
				continue;
			}

			var parsedRange = VersionRange.Parse(item.Range);
			var selected = feedList
				.SelectMany(feed => feed.Packages)
				.Where(p => string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase))
				.Select(p => (Package: p, Version: SemanticVersion.Parse(p.Version)))
				.Where(p => parsedRange.Allows(p.Version))
				.OrderByDescending(p => p.Version)
				.Select(p => p.Package)
				.FirstOrDefault()
				?? throw new PackageException(PackageDiagnosticIds.VersionConflict, $"Package '{item.Id}' satisfying '{item.Range}' was not found in local feeds.");

			foreach (var requirement in ranges)
			{
				if (!VersionRange.Parse(requirement.Range).Allows(SemanticVersion.Parse(selected.Version)))
					ThrowConflict(item.Id, ranges);
			}

			var package = new ResolvedPackage(selected.Id, selected.Version, selected.Hash, selected.FullPath, selected.Dependencies);
			resolved[selected.Id] = package;
			foreach (var dependency in selected.Dependencies)
				queue.Enqueue((dependency.Id, dependency.Version, selected.Id));
		}

		return new ResolvedGraph(feedList.Select(f => "file://" + f.RootPath).ToArray(), resolved);
	}

	public static LockFile ToLockFile(ResolvedGraph graph)
	{
		return new LockFile
		{
			Sources = graph.Sources,
			Packages = graph.Packages.ToDictionary(
				p => p.Key,
				p => new LockedPackage(
					p.Value.Version,
					p.Value.ContentHash,
					p.Value.Dependencies.ToDictionary(d => d.Id, d => d.Version, StringComparer.OrdinalIgnoreCase)),
				StringComparer.OrdinalIgnoreCase)
		};
	}

	private static void ThrowConflict(string id, List<(string Parent, string Range)> ranges)
	{
		var first = ranges[0];
		var second = ranges.FirstOrDefault(r => r.Range != first.Range);
		if (second == default) second = ranges[^1];
		throw new PackageException(PackageDiagnosticIds.VersionConflict, $"Version conflict: '{first.Parent}' requires '{id} {first.Range}', '{second.Parent}' requires '{id} {second.Range}'.");
	}
}
