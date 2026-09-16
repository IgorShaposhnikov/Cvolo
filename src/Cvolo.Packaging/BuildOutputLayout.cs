namespace Cvolo.Packaging;

/// <summary>
/// Centralizes the on-disk layout used by normal Cvolo builds.
/// Final runnable/native artifacts live under bin/&lt;configuration&gt;, while
/// compiler intermediates and diagnostics live under obj/&lt;configuration&gt;.
/// Host-targeted .cvlib artifacts keep their target-triple subdirectory so
/// multiple package slices can coexist without changing the executable layout.
/// </summary>
public static class BuildOutputLayout
{
	public const string DefaultConfiguration = "Debug";

	public static string GetBinRoot(string projectDirectory)
	{
		return Path.Combine(Path.GetFullPath(projectDirectory), "bin");
	}

	public static string GetObjRoot(string projectDirectory)
	{
		return Path.Combine(Path.GetFullPath(projectDirectory), "obj");
	}

	public static string GetBinDirectory(string projectDirectory, string configuration = DefaultConfiguration)
	{
		return Path.Combine(GetBinRoot(projectDirectory), ValidateConfiguration(configuration));
	}

	public static string GetObjDirectory(string projectDirectory, string configuration = DefaultConfiguration)
	{
		return Path.Combine(GetObjRoot(projectDirectory), ValidateConfiguration(configuration));
	}

	public static string GetIntermediateIrPath(string projectDirectory, string outputName, string configuration = DefaultConfiguration)
	{
		return Path.Combine(GetObjDirectory(projectDirectory, configuration), ValidateOutputName(outputName) + ".ll");
	}

	public static string GetCompilationFailuresDirectory(string projectDirectory, string configuration = DefaultConfiguration)
	{
		return Path.Combine(GetObjDirectory(projectDirectory, configuration), "CompilationFailures");
	}

	public static string GetBuildStatePath(string projectDirectory, string configuration = DefaultConfiguration)
	{
		return Path.Combine(GetObjDirectory(projectDirectory, configuration), "cvolo.build-state.json");
	}

	public static string GetNativeOutputPath(string projectDirectory, string outputName, bool shared, string configuration = DefaultConfiguration)
	{
		var extension = shared
			? (OperatingSystem.IsWindows() ? ".dll" : ".so")
			: (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
		return Path.Combine(GetBinDirectory(projectDirectory, configuration), ValidateOutputName(outputName) + extension);
	}

	public static string GetPackageOutputPath(string projectDirectory, string outputName, string targetTriple, string configuration = DefaultConfiguration)
	{
		if (string.IsNullOrWhiteSpace(targetTriple))
			throw new ArgumentException("Target triple cannot be empty.", nameof(targetTriple));

		var targetDirectory = SanitizePathSegment(targetTriple);
		return Path.Combine(
			GetBinDirectory(projectDirectory, configuration),
			targetDirectory,
			ValidateOutputName(outputName) + ".cvlib");
	}

	private static string ValidateConfiguration(string configuration)
	{
		if (string.IsNullOrWhiteSpace(configuration))
			throw new ArgumentException("Configuration cannot be empty.", nameof(configuration));
		if (configuration.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
			|| configuration.Contains(Path.DirectorySeparatorChar)
			|| configuration.Contains(Path.AltDirectorySeparatorChar))
			throw new ArgumentException("Configuration must be a single path segment.", nameof(configuration));
		return configuration;
	}

	private static string ValidateOutputName(string outputName)
	{
		if (string.IsNullOrWhiteSpace(outputName))
			throw new ArgumentException("Output name cannot be empty.", nameof(outputName));
		if (outputName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
			|| outputName.Contains(Path.DirectorySeparatorChar)
			|| outputName.Contains(Path.AltDirectorySeparatorChar))
			throw new ArgumentException("Output name must be a single file name.", nameof(outputName));
		return outputName;
	}

	private static string SanitizePathSegment(string value)
	{
		return value.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
	}
}
