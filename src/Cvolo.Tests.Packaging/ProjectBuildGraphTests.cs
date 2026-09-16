using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class ProjectBuildGraphTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-build-graph-" + Guid.NewGuid().ToString("N"));

	public ProjectBuildGraphTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void Load_OrdersDependenciesBeforeConsumersAndDeduplicatesDiamond()
	{
		var common = CreateProject("Common", "namespace Common; public int Value() { return 42; }");
		var left = CreateProject("Left", "namespace Left; public int Value() { return 1; }", common);
		var right = CreateProject("Right", "namespace Right; public int Value() { return 2; }", common);
		var app = CreateProject("App", "int main() { return 0; }", left, right);

		var graph = ProjectBuildGraph.Load(app);

		Assert.Equal(4, graph.Nodes.Count);
		Assert.Equal("Common", Path.GetFileNameWithoutExtension(graph.Nodes[0].ProjectPath));
		Assert.Equal("App", Path.GetFileNameWithoutExtension(graph.Nodes[^1].ProjectPath));
		Assert.Equal(1, graph.Nodes.Count(n => Path.GetFileNameWithoutExtension(n.ProjectPath) == "Common"));
	}

	[Fact]
	public void Fingerprint_ChangesWhenTransitiveProjectSourceChanges()
	{
		var library = CreateProject("Library", "namespace Library; public int Value() { return 1; }");
		var app = CreateProject("App", "int main() { return 0; }", library);
		var before = ProjectBuildGraph.Load(app).Fingerprint;

		File.WriteAllText(Path.Combine(Path.GetDirectoryName(library)!, "Library.cvl"), "namespace Library; public int Value() { return 2; }");
		var after = ProjectBuildGraph.Load(app).Fingerprint;

		Assert.NotEqual(before, after);
	}

	[Fact]
	public void Fingerprint_ChangesWhenLockFileChanges()
	{
		var app = CreateProject("App", "int main() { return 0; }");
		var appDirectory = Path.GetDirectoryName(app)!;
		File.WriteAllText(Path.Combine(appDirectory, "cvolo.lock.json"), "{\"version\":1,\"packages\":{\"Foo\":1}}");
		var before = ProjectBuildGraph.Load(app).Fingerprint;

		File.WriteAllText(Path.Combine(appDirectory, "cvolo.lock.json"), "{\"version\":1,\"packages\":{\"Foo\":2}}");
		var after = ProjectBuildGraph.Load(app).Fingerprint;

		Assert.NotEqual(before, after);
	}

	[Fact]
	public void Fingerprint_IgnoresGeneratedBuildDirectories()
	{
		var app = CreateProject("App", "int main() { return 0; }");
		var appDirectory = Path.GetDirectoryName(app)!;
		var before = ProjectBuildGraph.Load(app).Fingerprint;

		Directory.CreateDirectory(Path.Combine(appDirectory, "obj", "Debug"));
		Directory.CreateDirectory(Path.Combine(appDirectory, "bin", "Debug"));
		Directory.CreateDirectory(Path.Combine(appDirectory, ".cvolo", "build"));
		File.WriteAllText(Path.Combine(appDirectory, "obj", "Debug", "Generated.cvl"), "int generated() { return 1; }");
		File.WriteAllText(Path.Combine(appDirectory, "bin", "Debug", "Generated.cvl"), "int generated() { return 2; }");
		File.WriteAllText(Path.Combine(appDirectory, ".cvolo", "build", "Generated.cvl"), "int generated() { return 3; }");
		var after = ProjectBuildGraph.Load(app).Fingerprint;

		Assert.Equal(before, after);
	}

	[Fact]
	public void IncrementalState_RequiresMatchingGraphOptionsAndOutput()
	{
		var app = CreateProject("App", "int main() { return 0; }");
		var graph = ProjectBuildGraph.Load(app);
		var output = Path.Combine(graph.ProjectDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "App.exe" : "App");
		Directory.CreateDirectory(Path.GetDirectoryName(output)!);
		File.WriteAllText(output, "artifact");

		Assert.False(IncrementalBuildState.IsUpToDate(graph, "build-v1|opt=Os", output));
		IncrementalBuildState.Record(graph, "build-v1|opt=Os", output);
		Assert.True(IncrementalBuildState.IsUpToDate(graph, "build-v1|opt=Os", output));
		Assert.False(IncrementalBuildState.IsUpToDate(graph, "build-v1|opt=O2", output));

		File.WriteAllText(Path.Combine(graph.ProjectDirectory, "App.cvl"), "int main() { return 1; }");
		var changedGraph = ProjectBuildGraph.Load(app);
		Assert.False(IncrementalBuildState.IsUpToDate(changedGraph, "build-v1|opt=Os", output));

		File.Delete(output);
		Assert.False(IncrementalBuildState.IsUpToDate(graph, "build-v1|opt=Os", output));
	}

	[Fact]
	public void Fingerprint_IsStableAcrossEquivalentCheckoutRoots()
	{
		var firstRoot = Path.Combine(_root, "checkout-a");
		var secondRoot = Path.Combine(_root, "checkout-b");
		Directory.CreateDirectory(firstRoot);
		Directory.CreateDirectory(secondRoot);

		var first = CreateGraphUnder(firstRoot);
		var second = CreateGraphUnder(secondRoot);

		Assert.Equal(ProjectBuildGraph.Load(first).Fingerprint, ProjectBuildGraph.Load(second).Fingerprint);
	}

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string CreateGraphUnder(string root)
	{
		var originalRoot = _root;
		var libraryDirectory = Path.Combine(root, "Lib");
		var appDirectory = Path.Combine(root, "App");
		Directory.CreateDirectory(libraryDirectory);
		Directory.CreateDirectory(appDirectory);
		var library = WriteProject(libraryDirectory, "Lib", "namespace Lib; public int Value() { return 7; }");
		return WriteProject(appDirectory, "App", "int main() { return 0; }", library);
	}

	private string CreateProject(string name, string source, params string[] references)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		return WriteProject(directory, name, source, references);
	}

	private static string WriteProject(string directory, string name, string source, params string[] references)
	{
		var projectPath = Path.Combine(directory, name + ".cvlproj");
		var referenceXml = string.Join(Environment.NewLine, references.Select(reference =>
			$"    <ProjectReference Include=\"{Path.GetRelativePath(directory, reference)}\" />"));
		var itemGroup = references.Length == 0 ? string.Empty : $"\n  <ItemGroup>\n{referenceXml}\n  </ItemGroup>";
		File.WriteAllText(projectPath, $$"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>{{(name == "App" ? "Exe" : "Library")}}</OutputType>
    <AssemblyName>{{name}}</AssemblyName>
  </PropertyGroup>{{itemGroup}}
</Project>
""");
		File.WriteAllText(Path.Combine(directory, name + ".cvl"), source);
		return projectPath;
	}
}
