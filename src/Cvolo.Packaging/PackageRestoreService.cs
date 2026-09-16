using System.Text.Json;

namespace Cvolo.Packaging;

public sealed record RestoreResult(
	string LockFilePath,
	bool LockFileUpdated,
	int InstalledPackages,
	int CachedPackages);

/// <summary>
/// Restores the package graph declared by a project. An existing in-sync lock file is
/// authoritative; a missing or out-of-sync lock file is resolved again from configured
/// local feeds and rewritten before packages are installed into the local cache.
/// </summary>
public sealed class PackageRestoreService(PackageInstaller installer)
{
	public RestoreResult Restore(ProjectManifest manifest)
	{
		ArgumentNullException.ThrowIfNull(manifest);

		var lockPath = LockFile.GetPath(manifest);
		var lockFileUpdated = false;
		LockFile lockFile;

		if (File.Exists(lockPath))
		{
			lockFile = LockFile.Read(lockPath);
			if (!PackageLockValidator.Validate(manifest, lockFile, out _))
			{
				lockFile = ResolveAndWriteLock(manifest, lockPath);
				lockFileUpdated = true;
			}
		}
		else
		{
			lockFile = ResolveAndWriteLock(manifest, lockPath);
			lockFileUpdated = true;
		}

		var (installed, cached) = InstallLockedPackages(manifest, lockFile);
		return new RestoreResult(lockPath, lockFileUpdated, installed, cached);
	}

	private static LockFile ResolveAndWriteLock(ProjectManifest manifest, string lockPath)
	{
		var feeds = LoadConfiguredFeeds(manifest).ToList();
		var graph = new DependencyResolver().Resolve(manifest, feeds);
		var lockFile = DependencyResolver.ToLockFile(graph);
		lockFile.Write(lockPath);
		return lockFile;
	}

	private (int Installed, int Cached) InstallLockedPackages(ProjectManifest manifest, LockFile lockFile)
	{
		List<LocalFeed>? lockedFeeds = null;
		var installedCount = 0;
		var cachedCount = 0;

		foreach (var package in lockFile.Packages.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
		{
			if (installer.IsInstalled(package.Key, package.Value.Resolved))
			{
				var cached = installer.InstallFromCache(package.Key, package.Value.Resolved);
				if (!string.Equals(cached.ContentHash, package.Value.ContentHash, StringComparison.OrdinalIgnoreCase))
				{
					throw new PackageException(
						PackageDiagnosticIds.CachedContentMismatch,
						$"Cached package '{package.Key}@{package.Value.Resolved}' does not match cvolo.lock.json.");
				}

				cachedCount++;
				continue;
			}

			lockedFeeds ??= lockFile.Sources
				.Select(source => LocalFeed.Load(source, manifest.ProjectDirectory))
				.ToList();

			var feedPackage = lockedFeeds
				.Select(feed => feed.FindBest(package.Key, package.Value.Resolved))
				.FirstOrDefault(candidate => candidate is not null)
				?? throw new PackageException(
					PackageDiagnosticIds.VersionConflict,
					$"Package '{package.Key}@{package.Value.Resolved}' was not found in the feeds recorded by cvolo.lock.json.");

			if (!string.Equals(feedPackage.Hash, package.Value.ContentHash, StringComparison.OrdinalIgnoreCase))
			{
				throw new PackageException(
					PackageDiagnosticIds.CachedContentMismatch,
					$"Package '{package.Key}@{package.Value.Resolved}' feed content does not match cvolo.lock.json.");
			}

			installer.InstallFromFile(feedPackage.FullPath);
			installedCount++;
		}

		return (installedCount, cachedCount);
	}

	private static IEnumerable<LocalFeed> LoadConfiguredFeeds(ProjectManifest manifest)
	{
		if (!string.IsNullOrWhiteSpace(manifest.LocalFeed))
			yield return LocalFeed.Load(manifest.LocalFeed, manifest.ProjectDirectory);

		var global = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cvolo", "sources.json");
		if (!File.Exists(global))
			yield break;

		foreach (var source in JsonSerializer.Deserialize<string[]>(File.ReadAllText(global)) ?? [])
			yield return LocalFeed.Load(source, manifest.ProjectDirectory);
	}
}
