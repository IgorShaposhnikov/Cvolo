using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

/// <summary>
/// Loads developer-built ProjectReference .cvlib artifacts as ordinary Cvolo module inputs.
/// These artifacts bypass the package cache/lock file because their lifetime is the local
/// project graph, but they still use the same Sector 1 API metadata and Sector 3 bitcode model.
/// </summary>
public static class ProjectReferenceArtifactLoader
{
	public static IReadOnlyList<ResolvedPackageArtifacts> Load(
		string pathOrDirectory,
		string configuration = BuildOutputLayout.DefaultConfiguration)
	{
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);
		var graph = ProjectGraph.Load(pathOrDirectory);
		if (graph.Nodes.Count <= 1)
			return [];

		var extractionRoot = Path.Combine(
			graph.ProjectDirectory,
			".cvolo",
			"build",
			"project-references",
			configuration);
		Directory.CreateDirectory(extractionRoot);

		var artifacts = new List<ResolvedPackageArtifacts>(graph.Nodes.Count - 1);
		foreach (var node in graph.Nodes)
		{
			if (string.Equals(node.ProjectPath, graph.RootProjectPath, StringComparison.OrdinalIgnoreCase))
				continue;

			var manifest = ProjectManifest.Load(node.ProjectPath);
			if (!manifest.IsLibrary)
				throw new InvalidOperationException($"ProjectReference '{node.ProjectPath}' must be a library project to be consumed as a build artifact.");

			var artifactPath = LibraryBuildPipeline.GetOutputPath(manifest, configuration, TargetTriple.HostTriple());
			if (!File.Exists(artifactPath))
				throw new FileNotFoundException(
					$"ProjectReference artifact '{artifactPath}' is missing. Build the referenced project first.",
					artifactPath);

			artifacts.Add(LoadArtifact(manifest, artifactPath, extractionRoot));
		}

		return artifacts;
	}

	private static ResolvedPackageArtifacts LoadArtifact(ProjectManifest manifest, string artifactPath, string extractionRoot)
	{
		try
		{
			// Normal developer library builds are intentionally unsigned. Structural and Merkle
			// verification still run here; only the all-zero signature block is permitted.
			using var archive = CvlArchiveReader.Read(artifactPath, allowUnsigned: true);
			var triple = TargetTriple.HostTriple();
			var slice = archive.Manifest.Slices.SingleOrDefault(entry =>
				string.Equals(entry.Triple, triple, StringComparison.Ordinal));
			if (slice is null || slice.Sector3.Length == 0 || archive.Sectors.Count < 3)
			{
				throw new PackageException(
					PackageDiagnosticIds.BitcodeUnavailable,
					$"ProjectReference '{manifest.PackageId}' has no Sector 3 bitcode for host triple '{triple}'.",
					$"Rebuild '{manifest.ProjectPath}' for the current host before building its consumer.");
			}

			var apiMetadata = PackageApiMetadata.Read(archive);
			var templateUnits = PackageTemplateSource.Read(archive, manifest.PackageId, manifest.Version);
			var projectKey = Convert.ToHexStringLower(archive.MerkleRootHash);
			var packageDirectory = Path.Combine(
				extractionRoot,
				Sanitize(manifest.PackageId),
				projectKey);
			Directory.CreateDirectory(packageDirectory);

			var bitcodePath = Path.Combine(packageDirectory, manifest.OutputName + ".bc");
			ExtractSlice(archive, sectorIndex: 2, slice.Sector3, bitcodePath);

			string? objectPath = null;
			if (slice.Sector2.Length > 0 && archive.Sectors.Count >= 2)
			{
				var extension = OperatingSystem.IsWindows() ? ".obj" : ".o";
				objectPath = Path.Combine(packageDirectory, manifest.OutputName + extension);
				ExtractSlice(archive, sectorIndex: 1, slice.Sector2, objectPath);
			}

			return new ResolvedPackageArtifacts(
				manifest.PackageId,
				manifest.Version,
				objectPath,
				bitcodePath,
				apiMetadata,
				apiMetadata.NativeLibraries,
				templateUnits,
				[],
				UsesSourceFallback: false);
		}
		catch (CvlFormatException ex)
		{
			throw new PackageException(
				ex.Code,
				$"ProjectReference artifact '{artifactPath}' failed archive verification.",
				ex.Detail);
		}
	}

	private static void ExtractSlice(CvlArchive archive, int sectorIndex, CvlSectorRange range, string outputPath)
	{
		if (File.Exists(outputPath))
			return;

		var length = checked((int)range.Length);
		var absoluteOffset = checked(archive.Sectors[sectorIndex].Offset + range.Offset);
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
				// Another build published the same immutable artifact first.
			}
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}

	private static string Sanitize(string value)
	{
		foreach (var invalid in Path.GetInvalidFileNameChars())
			value = value.Replace(invalid, '_');
		return value;
	}
}
