using System.Text.Json;
using Blake3;
using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

public sealed record InstallResult(string PackageId, string Version, string OutputPath, string Source, string ContentHash, bool AlreadyInstalled, bool WasUnsigned);
public sealed record InstalledPackageMetadata(string PackageId, string Version, string Source, string ContentHash, DateTimeOffset InstalledAt);

/// <summary>
/// Owns a cache entry whose .cvlib has been structurally, Merkle, and Ed25519 verified.
/// The archive remains mapped until this object is disposed so callers can consume the
/// already-verified host slice without reopening or reverifying the package.
/// </summary>
public sealed class VerifiedCachedPackage(InstallResult result, CvlArchive archive) : IDisposable
{
	public InstallResult Result { get; } = result;
	public CvlArchive Archive { get; } = archive;

	public void Dispose() => Archive.Dispose();
}

public sealed class PackageInstaller(PackageCache cache)
{
	public InstallResult InstallFromFile(string cvlibPath)
	{
		var source = Path.GetFullPath(cvlibPath);
		// Keep the source immutable while hashing, verifying and copying it.
		using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var hasher = Hasher.New();
		var buffer = new byte[81920];
		int count;
		while ((count = input.Read(buffer)) != 0) hasher.Update(buffer.AsSpan(0, count));
		var hash = "blake3:" + Convert.ToHexStringLower(hasher.Finalize().AsSpan());

		// v0.2.6 trust contract: install consumes only a package whose Ed25519 signature
		// and Merkle root verify. CvlArchiveReader maps CVLF1902/CVLF1904 as appropriate.
		using var archive = CvlArchiveReader.Read(source);
		var metadata = PackageMetadata.Read(archive);
		var triple = TargetTriple.HostTriple();
		var hostSlice = archive.Manifest.Slices.SingleOrDefault(s => string.Equals(s.Triple, triple, StringComparison.Ordinal));
		if (hostSlice is null || (hostSlice.Sector2.Length == 0 && hostSlice.Sector3.Length == 0))
		{
			throw new PackageException(
				CvlFormatDiagnosticIds.MissingTargetSlice,
				$"No usable slice for host triple '{triple}' in '{source}'.");
		}

		var destination = cache.GetPackageDirectory(metadata.PackageId, metadata.Version);
		var parent = Path.GetDirectoryName(destination)!;
		Directory.CreateDirectory(parent);
		// Serialize installations and index updates for this package across processes.
		using var installLock = new FileStream(Path.Combine(parent, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		if (Directory.Exists(destination))
		{
			var existing = InstallFromCache(metadata.PackageId, metadata.Version);
			if (!string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
				throw new PackageException(PackageDiagnosticIds.CachedContentMismatch, $"Package '{metadata.PackageId}@{metadata.Version}' already cached with different content hash.");
			return existing with { Source = source };
		}

		var temporary = destination + ".tmp_" + Guid.NewGuid().ToString("N");
		Directory.CreateDirectory(temporary);
		var name = metadata.PackageId + ".cvlib";
		try
		{
			var output = Path.Combine(temporary, name);
			if (archive.Manifest.Slices.Count == 1)
			{
				// The source archive has already been verified once above. Do not verify the
				// identical copied bytes a second time; validate the cache shape on that mapping.
				ValidateCacheSliceInvariant(archive, metadata.PackageId, metadata.Version);
				File.Copy(source, output);
			}
			else
			{
				// Install-time thinning is mandatory in cvlib v0.2.6. ThinPipeline also
				// rebases the host ranges and signs the cache-local archive with the machine key.
				using var key = cache.LoadSigningKey();
				ThinPipeline.Write(archive, triple, output, key);

				// This is newly-generated content, so verify it once before publishing it.
				using var installedArchive = CvlArchiveReader.Read(output);
				ValidateCacheSliceInvariant(installedArchive, metadata.PackageId, metadata.Version);
			}

			File.WriteAllText(Path.Combine(temporary, ".metadata.json"), JsonSerializer.Serialize(
				new InstalledPackageMetadata(metadata.PackageId, metadata.Version, source, hash, DateTimeOffset.UtcNow)));
			Directory.Move(temporary, destination);
			var versions = Directory.GetDirectories(parent).Where(d => File.Exists(Path.Combine(d, ".metadata.json")))
				.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
			var index = Path.Combine(parent, "index.json");
			File.WriteAllText(index + ".tmp", JsonSerializer.Serialize(versions));
			File.Move(index + ".tmp", index, true);
		}
		finally
		{
			if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
		}

		return new(metadata.PackageId, metadata.Version, Path.Combine(destination, name), source, hash, false, false);
	}

	/// <summary>
	/// Opens a cached package exactly once, verifies its signature/Merkle tree, validates cache
	/// metadata and identity, and leaves the verified archive open for the caller to consume.
	/// </summary>
	public VerifiedCachedPackage OpenFromCache(string packageId, string version)
	{
		var directory = cache.GetPackageDirectory(packageId, version);
		var metadataPath = Path.Combine(directory, ".metadata.json");
		InstalledPackageMetadata metadata;
		try
		{
			metadata = JsonSerializer.Deserialize<InstalledPackageMetadata>(File.ReadAllText(metadataPath))
				?? throw new InvalidDataException("Cache metadata deserialized to null.");
		}
		catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
		{
			throw new PackageException(
				PackageDiagnosticIds.LockOutOfSync,
				$"Cached package metadata '{metadataPath}' is invalid; run 'cvolo pkg install'.",
				ex.Message);
		}

		PackageMetadata.ValidateIdentity(metadata.PackageId, metadata.Version);
		if (!string.Equals(packageId, metadata.PackageId, StringComparison.OrdinalIgnoreCase) || version != metadata.Version)
			throw new PackageException(PackageDiagnosticIds.PackageIdMismatch, "Cached package identity does not match its directory.");

		var path = Path.Combine(directory, metadata.PackageId + ".cvlib");
		var archive = CvlArchiveReader.Read(path);
		try
		{
			var identity = PackageMetadata.Read(archive);
			if (!string.Equals(identity.PackageId, metadata.PackageId, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(identity.Version, version, StringComparison.Ordinal))
			{
				throw new PackageException(PackageDiagnosticIds.PackageIdMismatch, "Archive identity does not match cache metadata.");
			}

			ValidateCacheSliceInvariant(archive, identity.PackageId, version);
			var result = new InstallResult(identity.PackageId, version, path, metadata.Source, metadata.ContentHash, true, false);
			return new VerifiedCachedPackage(result, archive);
		}
		catch
		{
			archive.Dispose();
			throw;
		}
	}

	public InstallResult InstallFromCache(string packageId, string version)
	{
		using var verified = OpenFromCache(packageId, version);
		return verified.Result;
	}

	public bool IsInstalled(string packageId, string version)
	{
		var directory = cache.GetPackageDirectory(packageId, version);
		return File.Exists(Path.Combine(directory, ".metadata.json")) && File.Exists(Path.Combine(directory, packageId + ".cvlib"));
	}

	private static void ValidateCacheSliceInvariant(CvlArchive archive, string packageId, string version)
	{
		var triple = TargetTriple.HostTriple();
		if (archive.Manifest.Slices.Count != 1 || !string.Equals(archive.Manifest.Slices[0].Triple, triple, StringComparison.Ordinal))
		{
			throw new PackageException(
				PackageDiagnosticIds.CacheNotThinned,
				$"Cached package '{packageId}@{version}' is not thinned to the host triple '{triple}'; reinstall the package.");
		}

		var slice = archive.Manifest.Slices[0];
		if (slice.Sector2.Length == 0 && slice.Sector3.Length == 0)
		{
			throw new PackageException(
				CvlFormatDiagnosticIds.MissingTargetSlice,
				$"Cached package '{packageId}@{version}' has no code for host triple '{triple}'.");
		}
	}
}
