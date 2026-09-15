using System.Text.Json;
using Blake3;
using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

public sealed record InstallResult(string PackageId, string Version, string OutputPath, string Source, string ContentHash, bool AlreadyInstalled, bool WasUnsigned);
public sealed record InstalledPackageMetadata(string PackageId, string Version, string Source, string ContentHash, DateTimeOffset InstalledAt);

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
		using var archive = OpenVerified(source, out var unsigned);
		var metadata = PackageMetadata.Read(archive);
		var triple = TargetTriple.HostTriple();
		if (!archive.Manifest.Slices.Any(s => s.Triple == triple))
			throw new PackageException(PackageDiagnosticIds.MissingHostSlice, $"No slice for host triple '{triple}' in '{source}'.");
		var destination = cache.GetPackageDirectory(metadata.PackageId, metadata.Version);
		var parent = Path.GetDirectoryName(destination)!;
		Directory.CreateDirectory(parent);
		// Serialize installations and index updates for this package across processes.
		using var installLock = new FileStream(Path.Combine(parent, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		if (Directory.Exists(destination))
		{
			var existing = InstallFromCache(metadata.PackageId, metadata.Version);
			if (existing.ContentHash != hash)
				throw new PackageException(PackageDiagnosticIds.CachedContentMismatch, $"Package '{metadata.PackageId}@{metadata.Version}' already cached with different content hash.");
			return existing with { Source = source, WasUnsigned = unsigned };
		}
		var temporary = destination + ".tmp_" + Guid.NewGuid().ToString("N");
		Directory.CreateDirectory(temporary);
		var name = metadata.PackageId + ".cvlib";
		try
		{
			var output = Path.Combine(temporary, name);
			if (archive.Manifest.Slices.Count == 1)
				File.Copy(source, output);
			else
			{
				using var key = cache.LoadSigningKey();
				ThinPipeline.Write(archive, triple, output, key);
			}
			using (OpenVerified(output, out _)) { }
			File.WriteAllText(Path.Combine(temporary, ".metadata.json"), JsonSerializer.Serialize(
				new InstalledPackageMetadata(metadata.PackageId, metadata.Version, source, hash, DateTimeOffset.UtcNow)));
			Directory.Move(temporary, destination);
			var versions = Directory.GetDirectories(parent).Where(d => File.Exists(Path.Combine(d, ".metadata.json")))
				.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
			var index = Path.Combine(parent, "index.json");
			File.WriteAllText(index + ".tmp", JsonSerializer.Serialize(versions));
			File.Move(index + ".tmp", index, true);
		}
		finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
		return new(metadata.PackageId, metadata.Version, Path.Combine(destination, name), source, hash, false, unsigned);
	}

	public InstallResult InstallFromCache(string packageId, string version)
	{
		var directory = cache.GetPackageDirectory(packageId, version);
		var metadata = JsonSerializer.Deserialize<InstalledPackageMetadata>(File.ReadAllText(Path.Combine(directory, ".metadata.json")))
			?? throw new InvalidDataException("Invalid cache metadata.");
		PackageMetadata.ValidateIdentity(metadata.PackageId, metadata.Version);
		if (!string.Equals(packageId, metadata.PackageId, StringComparison.OrdinalIgnoreCase) || version != metadata.Version)
			throw new PackageException(PackageDiagnosticIds.PackageIdMismatch, "Cached package identity does not match its directory.");
		var path = Path.Combine(directory, metadata.PackageId + ".cvlib");
		using var archive = OpenVerified(path, out var unsigned);
		var identity = PackageMetadata.Read(archive);
		if (identity.PackageId != metadata.PackageId || identity.Version != version)
			throw new PackageException(PackageDiagnosticIds.PackageIdMismatch, "Archive identity does not match cache metadata.");
		return new(identity.PackageId, version, path, metadata.Source, metadata.ContentHash, true, unsigned);
	}

	public bool IsInstalled(string packageId, string version)
	{
		var directory = cache.GetPackageDirectory(packageId, version);
		return File.Exists(Path.Combine(directory, ".metadata.json")) && File.Exists(Path.Combine(directory, packageId + ".cvlib"));
	}

	private static CvlArchive OpenVerified(string path, out bool unsigned)
	{
		// Structural and Merkle checks still run for unsigned inputs.
		using var probe = CvlArchiveReader.Read(path, verifySignature: false);
		using var stream = File.OpenRead(path);
		stream.Position = checked((long)probe.Header.SignatureOffset);
		Span<byte> signature = stackalloc byte[96];
		stream.ReadExactly(signature);
		unsigned = signature.IndexOfAnyExcept((byte)0) < 0;
		return CvlArchiveReader.Read(path, verifySignature: !unsigned);
	}
}
