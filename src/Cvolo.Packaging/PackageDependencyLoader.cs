using System.Text;
using Cvolo.Analysis.Symbols.FFI;
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
	string? BitcodePath,
	PackageApiMetadata ApiMetadata,
	IReadOnlyList<NativeLibraryInfo> NativeLibraries,
	IReadOnlyList<Cvolo.Core.AST.Base.CompilationUnitSyntax> TemplateUnits,
	IReadOnlyList<Cvolo.Core.AST.Base.CompilationUnitSyntax> SourceFallbackUnits,
	bool UsesSourceFallback,
	IReadOnlyList<PackageSourceFile>? SourceFiles = null);

public sealed record PackageSourceFile(
	string PackageId,
	string Version,
	string RelativePath,
	string FilePath,
	string Source);

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

		// The lock hash makes this directory content-addressed for the current dependency graph.
		// Never delete it at build start: another compiler process may be linking files from the
		// same graph. Individual artifacts are published atomically below and are immutable.
		Directory.CreateDirectory(extractionRoot);

		var artifacts = new List<ResolvedPackageArtifacts>(lockFile.Packages.Count);
		foreach (var package in lockFile.Packages
			.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
			.ThenBy(p => p.Value.Resolved, StringComparer.Ordinal))
		{
			artifacts.Add(LoadPackage(extractionRoot, package.Key, package.Value));
		}

		return artifacts;
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

			// Normal package builds consume Sector 3. If the host slice cannot provide bitcode,
			// Sector 5 becomes the implementation: compile the package sources in the consumer
			// rather than manufacturing an ABI boundary or failing an otherwise portable package.
			if (slice is null || slice.Sector3.Length == 0 || archive.Sectors.Count < 3)
			{
				var fallbackUnits = PackageTemplateSource.ReadAll(archive, packageId, locked.Resolved);
				if (fallbackUnits.Count == 0)
				{
					var code = slice is null ? CvlFormatDiagnosticIds.MissingTargetSlice : PackageDiagnosticIds.BitcodeUnavailable;
					throw new PackageException(
						code,
						slice is null
							? $"Cached package '{packageId}@{locked.Resolved}' has no host slice for '{triple}' and no Sector 5 fallback."
							: $"Package '{packageId}@{locked.Resolved}' has no Sector 3 bitcode for host triple '{triple}' and no Sector 5 fallback.");
				}

				return new ResolvedPackageArtifacts(
					packageId, locked.Resolved, null, null, new PackageApiMetadata(), [], [], fallbackUnits, UsesSourceFallback: true);
			}

			var apiMetadata = PackageApiMetadata.Read(archive);
			var templateUnits = PackageTemplateSource.Read(archive, packageId, locked.Resolved);
			var packageDirectory = Path.Combine(extractionRoot, packageId.ToLowerInvariant(), locked.Resolved);
			Directory.CreateDirectory(packageDirectory);
			var sourceFiles = ExtractSourceFiles(archive, packageId, locked.Resolved, packageDirectory);

			var bitcodePath = Path.Combine(packageDirectory, packageId + ".bc");
			ExtractSlice(archive, sectorIndex: 2, slice.Sector3, bitcodePath, packageId, locked.Resolved, "Sector 3 bitcode");

			string? objectPath = null;
			if (slice.Sector2.Length > 0 && archive.Sectors.Count >= 2)
			{
				var objectExtension = OperatingSystem.IsWindows() ? ".obj" : ".o";
				objectPath = Path.Combine(packageDirectory, packageId + objectExtension);
				ExtractSlice(archive, sectorIndex: 1, slice.Sector2, objectPath, packageId, locked.Resolved, "Sector 2 native object");
			}

			return new ResolvedPackageArtifacts(
				packageId, locked.Resolved, objectPath, bitcodePath, apiMetadata, apiMetadata.NativeLibraries, templateUnits, [], UsesSourceFallback: false, sourceFiles);
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

	private static IReadOnlyList<PackageSourceFile> ExtractSourceFiles(
		CvlArchive archive,
		string packageId,
		string version,
		string packageDirectory)
	{
		var sourceRoot = Path.GetFullPath(Path.Combine(packageDirectory, "source"));
		Directory.CreateDirectory(sourceRoot);
		var rootPrefix = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		var pathComparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
		var sourceFiles = new List<PackageSourceFile>();

		foreach (var sourceFile in PackageSourceBundle.Parse(archive.ReadSourceBuffer()))
		{
			var relativePath = sourceFile.RelativePath.Replace('\\', '/');
			var segments = relativePath.Split('/');
			if (relativePath.Length == 0 ||
				Path.IsPathRooted(relativePath) ||
				segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
			{
				throw new PackageException(
					PackageDiagnosticIds.InvalidSourceBundle,
					$"Package '{packageId}@{version}' contains an invalid source path.");
			}

			if (!seenPaths.Add(relativePath))
			{
				throw new PackageException(
					PackageDiagnosticIds.InvalidSourceBundle,
					$"Package '{packageId}@{version}' contains duplicate source path '{relativePath}'.");
			}

			var filePath = Path.GetFullPath(Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
			if (!filePath.StartsWith(rootPrefix, pathComparer))
			{
				throw new PackageException(
					PackageDiagnosticIds.InvalidSourceBundle,
					$"Package '{packageId}@{version}' source path '{relativePath}' escapes its extraction directory.");
			}

			Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
			WriteSourceFile(filePath, sourceFile.Source);
			sourceFiles.Add(new PackageSourceFile(packageId, version, relativePath, filePath, sourceFile.Source));
		}

		return sourceFiles;
	}

	private static void WriteSourceFile(string outputPath, string source)
	{
		if (File.Exists(outputPath))
			return;

		var temporaryPath = outputPath + ".tmp_" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporaryPath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			try
			{
				File.Move(temporaryPath, outputPath);
			}
			catch (IOException) when (File.Exists(outputPath))
			{
				return;
			}
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
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

		if (File.Exists(outputPath))
			return;

		var bytes = archive.GetRawSlice(absoluteOffset, length);
		var temporaryPath = outputPath + ".tmp_" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllBytes(temporaryPath, bytes.ToArray());
			try
			{
				File.Move(temporaryPath, outputPath);
			}
			catch (IOException) when (File.Exists(outputPath))
			{
				// Another build published the exact content-addressed artifact first. Reuse it.
			}
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}

	private static PackageException MissingFromCache(string packageId, string version) =>
		new(
			PackageDiagnosticIds.LockOutOfSync,
			$"Locked package '{packageId}@{version}' is missing from the local cache; run 'cvolo pkg install'.");
}
