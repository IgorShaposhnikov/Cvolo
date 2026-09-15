using System.Xml.Linq;

namespace Cvolo.Packaging;

/// <summary>
/// A package reference declared in a .cvlproj &lt;ItemGroup&gt;.
/// </summary>
/// <param name="Id">The package ID (the &lt;Include&gt; value).</param>
/// <param name="Version">The requested version expression (exact, ^, ~, or floating like 1.*).</param>
public sealed record PackageReference(string Id, string Version);

/// <summary>
/// The package metadata and source layout of a Cvolo project, loaded from its .cvlproj.
/// The project file is located by walking up from the given path-or-directory.
/// </summary>
public sealed class ProjectManifest
{
	private ProjectManifest(
		string projectPath,
		string projectDirectory,
		string packageId,
		string version,
		string outputName,
		bool isLibrary,
		bool strictOption,
		IReadOnlyList<string> targetFrameworks,
		IReadOnlyList<PackageReference> dependencies)
	{
		ProjectPath = projectPath;
		ProjectDirectory = projectDirectory;
		PackageId = packageId;
		Version = version;
		OutputName = outputName;
		IsLibrary = isLibrary;
		StrictOption = strictOption;
		TargetFrameworks = targetFrameworks;
		Dependencies = dependencies;
	}

	/// <summary>The absolute path of the .cvlproj file.</summary>
	public string ProjectPath { get; }

	/// <summary>The directory containing the .cvlproj file.</summary>
	public string ProjectDirectory { get; }

	/// <summary>The package ID (&lt;PackageId&gt;). Required; CVLP3000 when missing.</summary>
	public string PackageId { get; }

	/// <summary>The package version (&lt;Version&gt;). Required; CVLP3001 when missing, CVLP3002 when not valid SemVer.</summary>
	public string Version { get; }

	/// <summary>The assembly/output name; defaults to the .cvlproj file name without extension.</summary>
	public string OutputName { get; }

	/// <summary>True when &lt;OutputType&gt; is 'Library' (no main entry point required).</summary>
	public bool IsLibrary { get; }

	/// <summary>When &lt;StrictOption&gt; is true, optional syntax is rejected (mirrors CompilationProject).</summary>
	public bool StrictOption { get; }

	/// <summary>The target frameworks (&lt;TargetFrameworks&gt;), a semicolon-separated list of portable triples.</summary>
	public IReadOnlyList<string> TargetFrameworks { get; }

	/// <summary>The declared package references.</summary>
	public IReadOnlyList<PackageReference> Dependencies { get; }

	/// <summary>
	/// Loads and validates the nearest .cvlproj by walking up from <paramref name="pathOrDirectory"/>.
	/// </summary>
	public static ProjectManifest Load(string pathOrDirectory)
	{
		var fullPath = Path.GetFullPath(pathOrDirectory);
		var startDir = Directory.Exists(fullPath)
			? new DirectoryInfo(fullPath)
			: new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? fullPath);

		var projectFile = FindProject(startDir);
		if (projectFile is not null)
			return LoadFromProjectFile(projectFile);

		throw new FileNotFoundException($"Could not find a .cvlproj file from '{startDir.FullName}' or any parent directory.");
	}

	private static FileInfo? FindProject(DirectoryInfo start)
	{
		for (var dir = start; dir is not null; dir = dir.Parent)
		{
			var found = dir.GetFiles("*.cvlproj").FirstOrDefault();
			if (found is not null)
				return found;
		}

		return null;
	}

	private static ProjectManifest LoadFromProjectFile(FileInfo projectFile)
	{
		XDocument doc;
		try
		{
			doc = XDocument.Load(projectFile.FullName);
		}
		catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
		{
			throw new PackageException(PackageDiagnosticIds.InvalidSemVer, $"Failed to read '{projectFile.Name}'.", ex.Message);
		}

		var root = doc.Root ?? throw new PackageException(PackageDiagnosticIds.InvalidSemVer, $"'{projectFile.Name}' has no root element.");
		var propertyGroup = root.Elements("PropertyGroup").FirstOrDefault();

		var packageId = (string?)propertyGroup?.Element("PackageId");
		if (string.IsNullOrWhiteSpace(packageId))
			throw new PackageException(PackageDiagnosticIds.MissingPackageId, ".cvlproj is missing the required <PackageId> property.");

		// Keep the original layout vs. trimmed for output paths: trim whitespace, preserve internal case.
		var version = (string?)propertyGroup?.Element("Version");
		if (string.IsNullOrWhiteSpace(version))
			throw new PackageException(PackageDiagnosticIds.MissingVersion, ".cvlproj is missing the required <Version> property.");

		version = version.Trim();
		SemanticVersion.Parse(version); // throws CVLP3002 when invalid.

		var targetFrameworks = ((string?)propertyGroup?.Element("TargetFrameworks") ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		var outputName = (string?)propertyGroup?.Element("AssemblyName");
		if (string.IsNullOrWhiteSpace(outputName))
			outputName = Path.GetFileNameWithoutExtension(projectFile.Name);

		var outputType = (string?)propertyGroup?.Element("OutputType");
		var isLibrary = string.Equals(outputType, "Library", StringComparison.OrdinalIgnoreCase);

		var strictOptionValue = (string?)propertyGroup?.Element("StrictOption");
		var strictOption = string.Equals(strictOptionValue, "true", StringComparison.OrdinalIgnoreCase);

		var dependencies = new List<PackageReference>();
		foreach (var itemGroup in root.Elements("ItemGroup"))
		{
			foreach (var reference in itemGroup.Elements("PackageReference"))
			{
				var id = (string?)reference.Attribute("Include");
				if (string.IsNullOrWhiteSpace(id))
					continue;

				dependencies.Add(new PackageReference(id.Trim(), ((string?)reference.Attribute("Version"))?.Trim() ?? string.Empty));
			}
		}

		return new ProjectManifest(
			projectPath: projectFile.FullName,
			projectDirectory: projectFile.Directory?.FullName ?? throw new InvalidOperationException("Project file has no directory."),
			packageId: packageId.Trim(),
			version: version,
			outputName: outputName,
			isLibrary: isLibrary,
			strictOption: strictOption,
			targetFrameworks: targetFrameworks,
			dependencies: dependencies);
	}
}
