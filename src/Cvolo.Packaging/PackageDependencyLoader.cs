using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

/// <summary>
/// A verified, host-specific package artifact extracted from the local cache for linking.
/// Sector 3 bitcode is the Phase 4 build input. Sector 2 is retained when present for
/// future AOT/release paths but is not the primary dependency input for normal builds.
/// </summary>
public sealed record ResolvedPackageArtifacts(
	string PackageId,
	string Version,
	string? NativeObjectPath,
	string? BitcodePath);

/// <summary>
/// Resolves every package pinned by cvolo.lock.json from the local package cache,
/// verifies it at build start, and extracts deterministic host artifacts into a
/// project-local build directory.
/// </summary>
public sealed class PackageDependencyLoader(PackageCache cache, PackageInstaller installer)
{
	private readonly PackageCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
	private readonly PackageInstaller _installer = installer ?? throw new ArgumentNullException(nameof(installer));

	public IReadOnlyList<ResolvedPackageArtifacts> Load(ProjectManifest manifest, LockFile lockFile)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(lockFile);

		if (lockFile.Packages.Count == 0)
			return [];

		var lockPath = LockFile.GetPath(manifest);
		if (!File.Exists(lockPath))
			throw new PackageException(
				PackageDiagnosticIds.LockOutOfSync,
				"cvolo.lock.json is missing; run 'cvolo pkg install'.");

		var lockHash = LocalFeed.ComputeHash(lockPath);
		var lockHashHex = lockHash.StartsWith("blake3:", StringComparison.OrdinalIgnoreCase)
			? lockHash["blake3:".Length..]
			: lockHash;
		var extractionRoot = Path.Combine(manifest.ProjectDirectory, ".cvolo", "build", lockHashHex);

		// TODO(pkg-concurrency): builds sharing the same lock hash currently share this extraction
		// root. Parallel builds can delete/recreate each other's files. Add a cross-process lock
		// or extract to a per-process temp root and atomically publish it.
		if (Directory.Exists(extractionRoot))
			Directory.Delete(extractionRoot, recursive: true);
		Directory.CreateDirectory(extractionRoot);

		try
		{
			var artifacts = new List<ResolvedPackageArtifacts>(lockFile.Packages.Count);
			foreach (var package in lockFile.Packages
				.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
				.ThenBy(p => p.Value.Resolved, StringComparer.Ordinal))
			{
				artifacts.Add(LoadPackage(extractionRoot, package.Key, package.Value));
			}

			return artifacts;
		}
		catch
		{
			try
			{
				if (Directory.Exists(extractionRoot))
					Directory.Delete(extractionRoot, recursive: true);
			}
			catch
			{
				// Preserve the original package/load failure.
			}

			throw;
		}
	}

	private ResolvedPackageArtifacts LoadPackage(string extractionRoot, string packageId, LockedPackage locked)
	{
		var cacheDirectory = _cache.GetPackageDirectory(packageId, locked.Resolved);
		if (!File.Exists(Path.Combine(cacheDirectory, ".metadata.json")))
			throw MissingFromCache(packageId, locked.Resolved);

		try
		{
			// OpenFromCache performs the single build-start signature/Merkle verification
			// (or permitted unsigned-Merkle verification) and leaves that exact mapping open.
			using var cached = _installer.OpenFromCache(packageId, locked.Resolved);
			var installed = cached.Result;
			if (!string.Equals(installed.ContentHash, locked.ContentHash, StringComparison.OrdinalIgnoreCase))
			{
				throw new PackageException(
					PackageDiagnosticIds.BuildCacheContentMismatch,
					$"Cached package '{packageId}@{locked.Resolved}' does not match cvolo.lock.json.",
					$"Expected {locked.ContentHash}; cache metadata records {installed.ContentHash}. Run 'cvolo pkg install'.");
			}

			var archive = cached.Archive;
			var triple = TargetTriple.HostTriple();
			var slice = archive.Manifest.Slices.SingleOrDefault(s => string.Equals(s.Triple, triple, StringComparison.Ordinal));
			if (slice is null || (slice.Sector2.Length == 0 && slice.Sector3.Length == 0))
			{
				throw new PackageException(
					CvlFormatDiagnosticIds.MissingTargetSlice,
					$"Cached package '{packageId}@{locked.Resolved}' has no code for host triple '{triple}'.");
			}

			// Local Package Manager Phase 4 explicitly consumes Sector 3 bitcode during build.
			if (slice.Sector3.Length == 0 || archive.Sectors.Count < 3)
			{
				throw new PackageException(
					PackageDiagnosticIds.BitcodeUnavailable,
					$"Package '{packageId}@{locked.Resolved}' has no Sector 3 bitcode for host triple '{triple}'.",
					"Repack the dependency with bitcode enabled before building this project.");
			}

			var packageDirectory = Path.Combine(extractionRoot, packageId.ToLowerInvariant(), locked.Resolved);
			Directory.CreateDirectory(packageDirectory);

			var bitcodePath = Path.Combine(packageDirectory, packageId + ".bc");
			ExtractSlice(archive, sectorIndex: 2, slice.Sector3, bitcodePath, packageId, locked.Resolved, "Sector 3 bitcode");

			string? objectPath = null;
			if (slice.Sector2.Length > 0 && archive.Sectors.Count >= 2)
			{
				var objectExtension = OperatingSystem.IsWindows() ? ".obj" : ".o";
				objectPath = Path.Combine(packageDirectory, packageId + objectExtension);
				ExtractSlice(archive, sectorIndex: 1, slice.Sector2, objectPath, packageId, locked.Resolved, "Sector 2 native object");
			}

			return new ResolvedPackageArtifacts(packageId, locked.Resolved, objectPath, bitcodePath);
		}
		catch (CvlFormatException ex)
		{
			throw new PackageException(
				ex.Code,
				$"Cached package '{packageId}@{locked.Resolved}' failed archive verification.",
				ex.Detail);
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			throw MissingFromCache(packageId, locked.Resolved);
		}
		catch (InvalidDataException ex)
		{
			throw new PackageException(
				PackageDiagnosticIds.LockOutOfSync,
				$"Cached package '{packageId}@{locked.Resolved}' is invalid; run 'cvolo pkg install'.",
				ex.Message);
		}
	}

	private static void ExtractSlice(
		CvlArchive archive,
		int sectorIndex,
		CvlSectorRange range,
		string outputPath,
		string packageId,
		string version,
		string artifactName)
	{
		int length;
		try
		{
			length = checked((int)range.Length);
		}
		catch (OverflowException ex)
		{
			throw new PackageException(
				PackageDiagnosticIds.ArtifactRangeTooLarge,
				$"{artifactName} for '{packageId}@{version}' is too large to extract.",
				$"Slice length is {range.Length} bytes; maximum supported length is {int.MaxValue}. {ex.Message}");
		}

		ulong absoluteOffset;
		try
		{
			absoluteOffset = checked(archive.Sectors[sectorIndex].Offset + range.Offset);
		}
		catch (OverflowException ex)
		{
			throw new PackageException(
				PackageDiagnosticIds.ArtifactRangeTooLarge,
				$"{artifactName} for '{packageId}@{version}' has an invalid offset.",
				ex.Message);
		}

		var bytes = archive.GetRawSlice(absoluteOffset, length);
		File.WriteAllBytes(outputPath, bytes.ToArray());
	}

	private static PackageException MissingFromCache(string packageId, string version) =>
		new(
			PackageDiagnosticIds.LockOutOfSync,
			$"Locked package '{packageId}@{version}' is missing from the local cache; run 'cvolo pkg install'.");
}
