namespace Cvolo.Packaging;

/// <summary>
/// Produces the normal developer build artifact for a Cvolo library project.
/// Unlike <c>cvolo pack</c>, this is host-only, unsigned, and written into the
/// project's configuration/target output directory.
/// </summary>
public static class LibraryBuildPipeline
{
	public const string DefaultConfiguration = BuildOutputLayout.DefaultConfiguration;

	public static bool TryBuild(
		string pathOrDirectory,
		bool forceNativeShared,
		bool llvmOnly,
		bool emitIr,
		bool emitLowered,
		bool verbose,
		out PackResult? result)
	{
		result = null;

		// A direct source-file build remains a normal compiler invocation even when
		// that file happens to live below a library project directory.
		if (!Directory.Exists(pathOrDirectory)
			&& !pathOrDirectory.EndsWith(".cvlproj", StringComparison.OrdinalIgnoreCase))
			return false;

		// Explicit compiler-output modes keep their existing meaning. In particular,
		// --shared continues to request a native .dll/.so rather than a .cvlib.
		if (forceNativeShared || llvmOnly || emitIr || emitLowered)
			return false;

		ProjectManifest manifest;
		try
		{
			manifest = ProjectManifest.Load(pathOrDirectory);
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (PackageException ex) when (ex.Code is PackageDiagnosticIds.MissingPackageId or PackageDiagnosticIds.MissingVersion)
		{
			// `cvolo build` must retain normal compiler semantics for ordinary library
			// projects that are not package-ready yet. In particular, semantic
			// diagnostics (for example StrictOption) must not be hidden behind CVLP3000/1.
			// Once package identity is present, the dev build emits the host .cvlib.
			return false;
		}

		if (!manifest.IsLibrary)
			return false;

		var target = TargetTriple.HostTriple();
		var outputPath = GetOutputPath(manifest, DefaultConfiguration, target);
		result = PackPipeline.Execute(manifest.ProjectPath, new PackOptions
		{
			Targets = [target],
			OutputPath = outputPath,
			NoSign = true,
			Verbose = verbose
		});
		return true;
	}

	public static string GetOutputPath(ProjectManifest manifest, string configuration, string targetTriple)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		return BuildOutputLayout.GetPackageOutputPath(
			manifest.ProjectDirectory,
			manifest.OutputName,
			targetTriple,
			configuration);
	}

}
