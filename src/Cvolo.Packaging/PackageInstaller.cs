using System.Text.Json;
using Blake3;
using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

public sealed record InstallResult(string PackageId, string Version, string OutputPath, string Source, string ContentHash, bool AlreadyInstalled, bool WasUnsigned);
public sealed record InstalledPackageMetadata(string PackageId, string Version, string Source, string ContentHash, DateTimeOffset InstalledAt);

/// <summary>
/// Owns a cache entry whose .cvlib has passed structural and Merkle verification and,
/// when signed, Ed25519 verification. The archive remains mapped until disposal so callers
/// can consume the already-verified host slice without reopening or reverifying the package.
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
		// Hash first, then close the source stream before the archive reader maps it.
		string hash;
		using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
		using (var hasher = Hasher.New())
		{
			var buffer = new byte[81920];
			int count;
			while ((count = input.Read(buffer)) != 0) hasher.Update(buffer.AsSpan(0, count));
			hash = "blake3:" + Convert.ToHexStringLower(hasher.Finalize().AsSpan());
		}

		// The local package-manager increment permits unsigned local packages when no
		// trusted-key policy is configured. Structural/Merkle verification still runs.
		using var archive = CvlArchiveReader.Read(source, allowUnsigned: true);
		var unsigned = archive.IsUnsigned;
		TrustedKeyPolicy.Load(cache).Validate(archive, source);
		var metadata = PackageMetadata.Read(archive);
		var triple = TargetTriple.HostTriple();
		var hostSlice = archive.Manifest.Slices.SingleOrDefault(s => string.Equals(s.Triple, triple, StringComparison.Ordinal));
		var hasSourceFallback = !string.IsNullOrEmpty(archive.ReadSourceBuffer());
		if ((hostSlice is null || (hostSlice.Sector2.Length == 0 && hostSlice.Sector3.Length == 0)) && !hasSourceFallback)
		{
			throw new PackageException(
				PackageDiagnosticIds.MissingHostSlice,
				$"No usable slice for host triple '{triple}' in '{source}', and Sector 5 source fallback is unavailable.");
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
			return existing with { Source = source, WasUnsigned = unsigned };
		}

		var temporary = destination + ".tmp_" + Guid.NewGuid().ToString("N");
		Directory.CreateDirectory(temporary);
		var name = metadata.PackageId + ".cvlib";
		try
		{
			var output = Path.Combine(temporary, name);
			if (archive.Manifest.Slices.Count == 1 && hostSlice is not null)
			{
				// The source archive has already been verified once above. Do not verify the
				// identical copied bytes a second time; validate the cache shape on that mapping.
				ValidateCacheSliceInvariant(archive, metadata.PackageId, metadata.Version);
				File.Copy(source, output);
			}
			else
			{
				// Install-time thinning is mandatory for fat archives. The rewritten cache
				// archive is signed with the local machine key because thinning changes the root.
				using var key = cache.LoadSigningKey();
				if (hostSlice is not null)
					ThinPipeline.Write(archive, triple, output, key);
				else
					ThinPipeline.WriteSourceFallback(archive, triple, output, key);

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

		return new(metadata.PackageId, metadata.Version, Path.Combine(destination, name), source, hash, false, unsigned);
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
		var archive = CvlArchiveReader.Read(path, allowUnsigned: true);
		try
		{
			TrustedKeyPolicy.Load(cache).Validate(archive, $"{packageId}@{version}");

			var identity = PackageMetadata.Read(archive);
			if (!string.Equals(identity.PackageId, metadata.PackageId, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(identity.Version, version, StringComparison.Ordinal))
			{
				throw new PackageException(PackageDiagnosticIds.PackageIdMismatch, "Archive identity does not match cache metadata.");
			}

			ValidateCacheSliceInvariant(archive, identity.PackageId, version);
			var result = new InstallResult(identity.PackageId, version, path, metadata.Source, metadata.ContentHash, true, archive.IsUnsigned);
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
		if (slice.Sector2.Length == 0 && slice.Sector3.Length == 0 && string.IsNullOrEmpty(archive.ReadSourceBuffer()))
		{
			throw new PackageException(
				CvlFormatDiagnosticIds.MissingTargetSlice,
				$"Cached package '{packageId}@{version}' has neither host code nor Sector 5 source fallback.");
		}
	}
}
