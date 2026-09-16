namespace Cvolo.Packaging;

/// <summary>
/// Materializes package-ready ProjectReference libraries in dependency-first order and reuses
/// clean .cvlib artifacts. Projects without package identity keep the historical source-merge
/// behavior so adopting independent project artifacts remains backward compatible.
/// </summary>
public static class ProjectReferenceBuildPipeline
{
	public static ProjectReferenceBuildResult Prepare(
		ProjectBuildGraph graph,
		ProjectBuildPlan plan,
		string buildKey,
		string configuration = BuildOutputLayout.DefaultConfiguration,
		bool verbose = false)
	{
		ArgumentNullException.ThrowIfNull(graph);
		ArgumentNullException.ThrowIfNull(plan);
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);

		if (graph.Nodes.Count <= 1)
			return new ProjectReferenceBuildResult(false, 0, 0, []);

		var manifests = new Dictionary<string, ProjectManifest>(StringComparer.OrdinalIgnoreCase);
		foreach (var node in graph.Nodes)
		{
			if (string.Equals(node.ProjectPath, graph.RootProjectPath, StringComparison.OrdinalIgnoreCase))
				continue;

			ProjectManifest manifest;
			try
			{
				manifest = ProjectManifest.Load(node.ProjectPath);
			}
			catch (PackageException ex) when (ex.Code is PackageDiagnosticIds.MissingPackageId or PackageDiagnosticIds.MissingVersion)
			{
				if (verbose)
					Console.WriteLine($"ProjectReference artifact mode disabled: {Path.GetFileName(node.ProjectPath)} has no package identity.");
				return new ProjectReferenceBuildResult(false, 0, 0, []);
			}

			if (!manifest.IsLibrary)
			{
				if (verbose)
					Console.WriteLine($"ProjectReference artifact mode disabled: {Path.GetFileName(node.ProjectPath)} is not a library.");
				return new ProjectReferenceBuildResult(false, 0, 0, []);
			}

			manifests.Add(node.ProjectPath, manifest);
		}

		var planByProject = plan.Nodes.ToDictionary(node => node.Project.ProjectPath, StringComparer.OrdinalIgnoreCase);
		var outputs = new List<string>(manifests.Count);
		var built = 0;
		var reused = 0;

		foreach (var node in graph.Nodes)
		{
			if (!manifests.TryGetValue(node.ProjectPath, out var manifest))
				continue;

			var outputPath = LibraryBuildPipeline.GetOutputPath(manifest, configuration, TargetTriple.HostTriple());
			var requiresBuild = planByProject[node.ProjectPath].RequiresBuild || !File.Exists(outputPath);
			if (requiresBuild)
			{
				if (!LibraryBuildPipeline.TryBuild(
					node.ProjectPath,
					forceNativeShared: false,
					llvmOnly: false,
					emitIr: false,
					emitLowered: false,
					verbose,
					configuration,
					out var result))
				{
					throw new InvalidOperationException($"ProjectReference '{node.ProjectPath}' could not be built as a .cvlib artifact.");
				}

				outputPath = result!.OutputPath;
				ProjectBuildPlan.RecordSuccessful(node, buildKey, configuration);
				built++;
			}
			else
			{
				reused++;
			}

			outputs.Add(outputPath);
		}

		return new ProjectReferenceBuildResult(true, built, reused, outputs);
	}
}

public sealed record ProjectReferenceBuildResult(
	bool UseArtifacts,
	int BuiltProjects,
	int ReusedProjects,
	IReadOnlyList<string> ArtifactPaths);
