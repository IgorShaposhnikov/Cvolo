using System.Text.Json;
using Blake3;
using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

public sealed record LocalFeedPackage(string Id, string Version, string Hash, string File, string FullPath, IReadOnlyList<PackageReference> Dependencies);

public sealed class LocalFeed
{
	private readonly List<LocalFeedPackage> _packages;

	private LocalFeed(string rootPath, List<LocalFeedPackage> packages)
	{
		RootPath = rootPath;
		_packages = packages;
	}

	public string RootPath { get; }
	public IReadOnlyList<LocalFeedPackage> Packages => _packages;

	public static LocalFeed Load(string source, string projectDirectory)
	{
		var path = source.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? source[7..] : source;
		var root = Path.GetFullPath(path, projectDirectory);
		if (!Directory.Exists(root))
			throw new DirectoryNotFoundException($"Local feed '{root}' does not exist.");

		var indexPath = Path.Combine(root, "index.json");
		if (File.Exists(indexPath))
			return new LocalFeed(root, ReadIndex(root, indexPath));

		var packages = Directory.GetFiles(root, "*.cvlib", SearchOption.TopDirectoryOnly)
			.Select(ReadArchiveEntry)
			.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
			.ThenBy(p => SemanticVersion.Parse(p.Version))
			.ToList();
		return new LocalFeed(root, packages);
	}

	public LocalFeedPackage? FindBest(string id, string range)
	{
		var parsedRange = VersionRange.Parse(range);
		return _packages
			.Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
			.Select(p => (Package: p, Version: SemanticVersion.Parse(p.Version)))
			.Where(p => parsedRange.Allows(p.Version))
			.OrderByDescending(p => p.Version)
			.Select(p => p.Package)
			.FirstOrDefault();
	}

	private static List<LocalFeedPackage> ReadIndex(string root, string indexPath)
	{
		var index = JsonSerializer.Deserialize<FeedIndex>(File.ReadAllText(indexPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
			?? throw new InvalidDataException("Invalid feed index.");
		if (!string.Equals(index.Format, "cvolo.feed.v1", StringComparison.Ordinal))
			throw new InvalidDataException("Unsupported feed index format.");

		return index.Packages.Select(entry =>
		{
			var fullPath = Path.GetFullPath(entry.File, root);
			using var archive = CvlArchiveReader.Read(fullPath, verifySignature: false);
			var metadata = PackageMetadata.Read(archive);
			return new LocalFeedPackage(entry.Id, entry.Version, entry.Hash, entry.File, fullPath, metadata.Dependencies);
		}).ToList();
	}

	private static LocalFeedPackage ReadArchiveEntry(string path)
	{
		using var archive = CvlArchiveReader.Read(path, verifySignature: false);
		var metadata = PackageMetadata.Read(archive);
		return new LocalFeedPackage(metadata.PackageId, metadata.Version, ComputeHash(path), Path.GetFileName(path), path, metadata.Dependencies);
	}

	internal static string ComputeHash(string path)
	{
		using var stream = File.OpenRead(path);
		using var hasher = Hasher.New();
		var buffer = new byte[81920];
		int count;
		while ((count = stream.Read(buffer)) != 0) hasher.Update(buffer.AsSpan(0, count));
		return "blake3:" + Convert.ToHexStringLower(hasher.Finalize().AsSpan());
	}

	private sealed record FeedIndex(string Format, IReadOnlyList<FeedIndexPackage> Packages);
	private sealed record FeedIndexPackage(string Id, string Version, string Hash, string File);
}
