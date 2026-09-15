using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

/// <summary>
/// A verified, host-specific package artifact extracted from the local cache for linking.
/// </summary>
public sealed record ResolvedPackageArtifacts(string PackageId, string Version, string NativeObjectPath, string? BitcodePath);

/// <summary>
/// Resolves every package pinned by cvolo.lock.json from the local package cache,
/// verifies it at build start, and extracts the host native object into a deterministic
/// project-local build directory.
/// </summary>
public sealed class PackageDependencyLoader
{
	private readonly PackageCache _cache;
	private readonly PackageInstaller _installer;

	public PackageDependencyLoader(PackageCache cache, PackageInstaller installer)
	{
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		_installer = installer ?? throw new ArgumentNullException(nameof(installer));
	}

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
			// OpenFromCache performs the single build-start signature + Merkle verification and
			// leaves that exact verified mapping open for identity/slice consumption below.
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

			// A bitcode-only slice is valid per the cvlib manifest. It is not CVLF1901; this
			// increment links Sector 2 objects only, so diagnose the unimplemented consumer path.
			if (slice.Sector2.Length == 0)
			{
				throw new PackageException(
					PackageDiagnosticIds.NativeObjectUnavailable,
					$"Package '{packageId}@{locked.Resolved}' provides host bitcode but no Sector 2 native object.",
					"Sector 3 package-LTO consumption is not implemented yet.");
			}

			if (archive.Sectors.Count < 2)
			{
				throw new PackageException(
					PackageDiagnosticIds.NativeObjectUnavailable,
					$"Cached package '{packageId}@{locked.Resolved}' does not contain Sector 2 native object data.");
			}

			var packageDirectory = Path.Combine(extractionRoot, packageId.ToLowerInvariant(), locked.Resolved);
			Directory.CreateDirectory(packageDirectory);
			var objectExtension = OperatingSystem.IsWindows() ? ".obj" : ".o";
			var objectPath = Path.Combine(packageDirectory, packageId + objectExtension);

			int length;
			try
			{
				length = checked((int)slice.Sector2.Length);
			}
			catch (OverflowException ex)
			{
				throw new PackageException(
					PackageDiagnosticIds.ArtifactRangeTooLarge,
					$"Native object slice for '{packageId}@{locked.Resolved}' is too large to extract.",
					$"Sector 2 slice length is {slice.Sector2.Length} bytes; maximum supported length is {int.MaxValue}. {ex.Message}");
			}

			ulong absoluteOffset;
			try
			{
				absoluteOffset = checked(archive.Sectors[1].Offset + slice.Sector2.Offset);
			}
			catch (OverflowException ex)
			{
				throw new PackageException(
					PackageDiagnosticIds.ArtifactRangeTooLarge,
					$"Native object slice for '{packageId}@{locked.Resolved}' has an invalid offset.",
					ex.Message);
			}

			var objectBytes = archive.GetRawSlice(absoluteOffset, length);
			File.WriteAllBytes(objectPath, objectBytes.ToArray());

			// Sector 3 extraction is intentionally deferred until package bitcode/LTO consumption lands.
			return new ResolvedPackageArtifacts(packageId, locked.Resolved, objectPath, BitcodePath: null);
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

	private static PackageException MissingFromCache(string packageId, string version) =>
		new(
			PackageDiagnosticIds.LockOutOfSync,
			$"Locked package '{packageId}@{version}' is missing from the local cache; run 'cvolo pkg install'.");
}
