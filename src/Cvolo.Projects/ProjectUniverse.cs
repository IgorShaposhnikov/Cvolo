using Cvolo.Core.AST.Base;
using Cvolo.Core.Packages;
using Cvolo.Packaging;

namespace Cvolo.Projects;

/// <summary>
/// How an external (non-project-source) semantic unit participates in binding.
/// </summary>
public enum ExternalSemanticUnitKind
{
	/// <summary>A package API stub unit (Sector 1 metadata); no body.</summary>
	ExternalPackageApi,
	/// <summary>A package template/implementation unit compiled in the consumer.</summary>
	PackageTemplate,
}

/// <summary>
/// A parsed semantic unit that has no project source document (a package/artifact unit).
/// </summary>
public sealed record ExternalSemanticUnit(CompilationUnitSyntax Unit, ExternalSemanticUnitKind Kind);

/// <summary>
/// Inputs for building a project's semantic universe.
/// </summary>
public sealed record ProjectUniverseRequest(
	string InputPath,
	string? CompilerBaseDir = null,
	bool ForceShared = false,
	bool MergeProjectReferences = true,
	bool UseProjectReferencePackages = false,
	bool LoadPackages = true,
	string Configuration = BuildOutputLayout.DefaultConfiguration,
	PackageCache? PackageCache = null);

/// <summary>
/// The complete semantic input universe of one project: the project configuration, its
/// source files (project + standard library + merged ProjectReference sources) and the
/// external package/artifact units, each classified exactly as binding expects. This is the
/// single authoritative answer to "what belongs to this project's compilation".
/// </summary>
public sealed class ProjectUniverse
{
	public CompilationProject Project { get; }

	public IReadOnlyList<string> SourceFiles => Project.SourceFiles;

	/// <summary>Resolved package/artifact inputs (compiler-side detail; unused by read-only consumers).</summary>
	public IReadOnlyList<ResolvedPackageArtifacts> PackageArtifacts { get; }

	public IReadOnlyList<ExternalSemanticUnit> ExternalUnits { get; }

	internal ProjectUniverse(
		CompilationProject project,
		IReadOnlyList<ResolvedPackageArtifacts> packageArtifacts,
		IReadOnlyList<ExternalSemanticUnit> externalUnits)
	{
		Project = project;
		PackageArtifacts = packageArtifacts;
		ExternalUnits = externalUnits;
	}
}

/// <summary>
/// Builds a <see cref="ProjectUniverse"/> from a project input path. Both the compiler driver
/// and the language-server tooling call this so they observe the same semantic project universe.
/// </summary>
public static class ProjectUniverseLoader
{
	public static ProjectUniverse Load(ProjectUniverseRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		var project = CompilationProject.Load(
			request.InputPath,
			request.CompilerBaseDir,
			request.ForceShared,
			mergeProjectReferences: !request.UseProjectReferencePackages);

		var artifacts = new List<ResolvedPackageArtifacts>();

		if (request.LoadPackages)
		{
			var packageState = TryResolvePackageState(request.InputPath);
			if (packageState is { } state)
			{
				var cache = request.PackageCache ?? new PackageCache();
				var installer = new PackageInstaller(cache);
				artifacts.AddRange(new PackageDependencyLoader(cache, installer).Load(state.Manifest, state.LockFile));
			}

			if (request.UseProjectReferencePackages && project.ProjectReferences.Count > 0)
				artifacts.AddRange(ProjectReferenceArtifactLoader.Load(request.InputPath, request.Configuration));
		}

		if (artifacts.Count > 1)
		{
			artifacts = artifacts
				.DistinctBy(artifact => (
					artifact.PackageId,
					artifact.Version,
					BitcodePath: artifact.BitcodePath is null ? string.Empty : Path.GetFullPath(artifact.BitcodePath)))
				.ToList();
		}

		var externalUnits = new List<ExternalSemanticUnit>();
		foreach (var artifact in artifacts)
		{
			if (artifact.UsesSourceFallback)
			{
				// No host bitcode is usable: the verified Sector 5 sources become consumer-local
				// implementation units. Marking them as package-template units keeps their public
				// symbols internal to this module and avoids manufacturing a cross-package ABI.
				foreach (var sourceUnit in artifact.SourceFallbackUnits)
					externalUnits.Add(new ExternalSemanticUnit(sourceUnit, ExternalSemanticUnitKind.PackageTemplate));
				continue;
			}

			foreach (var packageUnit in artifact.ApiMetadata.CreateCompilationUnits(artifact.PackageId, artifact.Version))
				externalUnits.Add(new ExternalSemanticUnit(packageUnit, ExternalSemanticUnitKind.ExternalPackageApi));

			foreach (var templateUnit in artifact.TemplateUnits)
				externalUnits.Add(new ExternalSemanticUnit(templateUnit, ExternalSemanticUnitKind.PackageTemplate));
		}

		return new ProjectUniverse(project, artifacts, externalUnits);
	}

	private static (ProjectManifest Manifest, LockFile LockFile)? TryResolvePackageState(string inputPath)
	{
		if (File.Exists(inputPath) && inputPath.EndsWith(".cvl", StringComparison.OrdinalIgnoreCase))
			return null;

		ProjectManifest manifest;
		try
		{
			manifest = ProjectManifest.Load(inputPath);
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (PackageException ex) when (ex.Code is PackageDiagnosticIds.MissingPackageId or PackageDiagnosticIds.MissingVersion)
		{
			return null;
		}

		if (manifest.Dependencies.Count == 0)
			return null;

		var lockPath = LockFile.GetPath(manifest);
		if (!File.Exists(lockPath))
		{
			throw new PackageException(
				PackageDiagnosticIds.LockOutOfSync,
				"cvolo.lock.json is missing; run 'cvolo restore' or build/run without '--no-restore'.");
		}

		var lockFile = LockFile.Read(lockPath);
		if (!PackageLockValidator.Validate(manifest, lockFile, out var message))
		{
			throw new PackageException(
				PackageDiagnosticIds.LockOutOfSync,
				$"{message} Run 'cvolo restore' or build/run without '--no-restore'.");
		}

		return (manifest, lockFile);
	}
}
