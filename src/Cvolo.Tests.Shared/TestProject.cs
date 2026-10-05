using System.Text;
using System.Xml.Linq;

namespace Cvolo.Tests.Shared;

/// <summary>
/// Writable temporary Cvolo project used by compiler, packaging and tooling tests.
/// Project manifests are generated from <see cref="TestProjectSpec"/> and source files are copied
/// from <see cref="TestCases"/> rather than embedded in C# test code.
/// </summary>
public sealed class TestProject : IDisposable
{
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
	private readonly bool _ownsRoot;

	private TestProject(string rootPath, string projectFilePath, bool ownsRoot)
	{
		RootPath = rootPath;
		ProjectFilePath = projectFilePath;
		_ownsRoot = ownsRoot;
	}

	public string RootPath { get; }
	public string ProjectFilePath { get; }

	/// <summary>Creates or initializes a project at a caller-owned directory.</summary>
	public static TestProject CreateAt(string rootPath, TestProjectSpec? spec = null, bool ownsRoot = false)
	{
		spec ??= new TestProjectSpec();
		var root = Path.GetFullPath(rootPath);
		Directory.CreateDirectory(root);
		var projectFilePath = WriteProjectFile(root, spec);
		return new TestProject(root, projectFilePath, ownsRoot);
	}

	/// <summary>Creates an empty temporary project using the supplied manifest settings.</summary>
	public static TestProject Create(TestProjectSpec? spec = null, string? tempCategory = null)
	{
		spec ??= new TestProjectSpec();
		var category = string.IsNullOrWhiteSpace(tempCategory) ? "cvolo-tests" : tempCategory;
		var root = Path.Combine(Path.GetTempPath(), category, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var projectFilePath = WriteProjectFile(root, spec);
		return new TestProject(root, projectFilePath, ownsRoot: true);
	}

	/// <summary>
	/// Creates a temporary project and copies every file from one fixture directory into its root.
	/// </summary>
	public static TestProject FromFixtureDirectory(
		string fixtureDirectory,
		TestProjectSpec? spec = null,
		string? tempCategory = null)
	{
		var project = Create(spec, tempCategory);
		TestCases.CopyDirectory(fixtureDirectory, project.RootPath);
		return project;
	}

	/// <summary>
	/// Transitional helper for tests that still construct source dynamically. New tests should use
	/// <see cref="FromFixtureDirectory"/> or <see cref="CopyFixture"/> instead.
	/// </summary>
	public static TestProject FromSources(
		IEnumerable<(string FileName, string Source)> sources,
		TestProjectSpec? spec = null,
		string? tempCategory = null)
	{
		var project = Create(spec, tempCategory);
		foreach (var (fileName, source) in sources)
			project.WriteText(fileName, source);
		return project;
	}

	public string GetPath(string relativePath) => Path.Combine(
		RootPath,
		relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

	public string CopyFixture(string fixturePath, string? destinationRelativePath = null)
	{
		destinationRelativePath ??= Path.GetFileName(fixturePath);
		return TestCases.CopyFile(fixturePath, GetPath(destinationRelativePath));
	}

	public string WriteText(string relativePath, string content)
	{
		var path = GetPath(relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content, Utf8NoBom);
		return path;
	}

	public void Dispose()
	{
		if (!_ownsRoot)
			return;

		try
		{
			Directory.Delete(RootPath, recursive: true);
		}
		catch
		{
			// Best-effort cleanup; a cleanup failure must never hide the test result.
		}
	}

	private static string WriteProjectFile(string root, TestProjectSpec spec)
	{
		var propertyGroup = new XElement("PropertyGroup",
			new XElement("OutputType", spec.OutputType),
			new XElement("AssemblyName", spec.AssemblyName));

		if (!string.IsNullOrWhiteSpace(spec.PackageId))
			propertyGroup.Add(new XElement("PackageId", spec.PackageId));
		if (!string.IsNullOrWhiteSpace(spec.Version))
			propertyGroup.Add(new XElement("Version", spec.Version));

		foreach (var (name, value) in spec.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			propertyGroup.Add(new XElement(name, value));

		var project = new XElement("Project", new XAttribute("Sdk", "Cvolo.Sdk"), propertyGroup);

		if (spec.ProjectReferences.Count > 0)
		{
			project.Add(new XElement("ItemGroup",
				spec.ProjectReferences.Select(reference =>
					new XElement("ProjectReference", new XAttribute("Include", reference)))));
		}

		if (spec.PackageReferences.Count > 0)
		{
			project.Add(new XElement("ItemGroup",
				spec.PackageReferences.Select(reference =>
					new XElement("PackageReference",
						new XAttribute("Include", reference.Id),
						new XAttribute("Version", reference.Version)))));
		}

		var document = new XDocument(project);
		var projectFilePath = Path.Combine(root, spec.ProjectFileName);
		using var writer = new StreamWriter(projectFilePath, append: false, Utf8NoBom);
		document.Save(writer);
		return projectFilePath;
	}
}

public sealed record TestProjectSpec
{
	public string ProjectFileName { get; init; } = "Fixture.cvlproj";
	public string OutputType { get; init; } = "Exe";
	public string AssemblyName { get; init; } = "Fixture";
	public string? PackageId { get; init; }
	public string? Version { get; init; }
	public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
	public IReadOnlyList<string> ProjectReferences { get; init; } = [];
	public IReadOnlyList<TestPackageReference> PackageReferences { get; init; } = [];
}

public sealed record TestPackageReference(string Id, string Version);
