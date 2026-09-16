using System.Diagnostics;
using Cvolo.Drivers;
using Cvolo.Packaging;
using Cvolo.Projects;

namespace Cvolo.Tests.Packaging;

public sealed class ProjectReferenceIntegrationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-project-reference-tests", Guid.NewGuid().ToString("N"));

	public ProjectReferenceIntegrationTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void CompilationProject_DirectoryInput_LoadsDirectProjectReferenceSources()
	{
		var (appDirectory, libraryProject, librarySource) = CreateProjectPair();

		var project = CompilationProject.Load(appDirectory, compilerBaseDir: _root);

		Assert.Equal(Path.GetFullPath(libraryProject), Assert.Single(project.ProjectReferences));
		Assert.Contains(Path.GetFullPath(librarySource), project.SourceFiles);
		Assert.DoesNotContain(project.SourceFiles, file => file.EndsWith("cvolo.lock.json", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void CompilationProject_ArtifactMode_ExcludesReferencedSourcesButKeepsGraphIdentity()
	{
		var (appDirectory, libraryProject, librarySource) = CreateProjectPair();

		var project = CompilationProject.Load(appDirectory, compilerBaseDir: _root, mergeProjectReferences: false);

		Assert.Equal(Path.GetFullPath(libraryProject), Assert.Single(project.ProjectReferences));
		Assert.DoesNotContain(project.SourceFiles, file => string.Equals(file, Path.GetFullPath(librarySource), StringComparison.OrdinalIgnoreCase));
		Assert.Contains(project.SourceFiles, file => string.Equals(Path.GetFileName(file), "Main.cvl", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void CompilationProject_UsesCanonicalProjectGraphForReferencesAndSources()
	{
		var (appDirectory, _, _, _) = CreateTransitiveProjectGraph();
		var graph = ProjectGraph.Load(appDirectory);
		var project = CompilationProject.Load(appDirectory, compilerBaseDir: _root);

		var expectedReferences = graph.Nodes
			.Where(node => !string.Equals(node.ProjectPath, graph.RootProjectPath, StringComparison.OrdinalIgnoreCase))
			.Select(node => node.ProjectPath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		Assert.Equal(expectedReferences.Count, project.ProjectReferences.Count);
		Assert.All(project.ProjectReferences, reference => Assert.Contains(reference, expectedReferences));

		var graphSources = graph.Nodes.SelectMany(node => node.SourceFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Assert.All(graphSources, source => Assert.Contains(project.SourceFiles, candidate => string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public void Build_AppWithDirectProjectReference_NeedsNoPackFeedCacheOrLock()
	{
		RequireClang();
		var (appDirectory, _, _) = CreateProjectPair();
		Assert.False(File.Exists(Path.Combine(appDirectory, "cvolo.lock.json")));

		var cache = new PackageCache(Path.Combine(_root, "cache"));
		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, buildOutput) = CompileWithCapturedOutput(driver, appDirectory);
		Assert.True(exitCode == 0, buildOutput);

		var executable = Path.Combine(appDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "App.exe" : "App");
		Assert.True(File.Exists(executable), $"Expected compiler output '{executable}'.");
		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		});
		Assert.NotNull(process);
		Assert.True(process!.WaitForExit(10_000), $"Compiled app '{executable}' did not exit within 10 seconds.");
		Assert.Equal(0, process.ExitCode);
	}

	[Fact]
	public void CompilationProject_LoadsTransitiveProjectReferencesOnce()
	{
		var (appDirectory, libAProject, libBProject, libBSource) = CreateTransitiveProjectGraph();

		var project = CompilationProject.Load(appDirectory, compilerBaseDir: _root);

		Assert.Equal(2, project.ProjectReferences.Count);
		Assert.Contains(Path.GetFullPath(libAProject), project.ProjectReferences);
		Assert.Contains(Path.GetFullPath(libBProject), project.ProjectReferences);
		Assert.Equal(1, project.SourceFiles.Count(file => string.Equals(file, Path.GetFullPath(libBSource), StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public void Build_AppWithTransitiveProjectReference_CompilesWholeGraph()
	{
		RequireClang();
		var (appDirectory, _, _, _) = CreateTransitiveProjectGraph();

		var cache = new PackageCache(Path.Combine(_root, "transitive-cache"));
		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, buildOutput) = CompileWithCapturedOutput(driver, appDirectory);
		Assert.True(exitCode == 0, buildOutput);

		var executable = Path.Combine(appDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "App.exe" : "App");
		Assert.True(File.Exists(executable), $"Expected compiler output '{executable}'.");
		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		});
		Assert.NotNull(process);
		Assert.True(process!.WaitForExit(10_000), $"Compiled app '{executable}' did not exit within 10 seconds.");
		Assert.Equal(0, process.ExitCode);
	}

	[Fact]
	public void Build_PackageReadyTransitiveProjectReferences_UsesIndependentCvlibArtifacts()
	{
		RequireClang();
		var libB = CreatePackageReadyLibrary("ArtifactLibB", "namespace ArtifactLibB; public int BaseValue() { return 40; }");
		var libA = CreatePackageReadyLibrary(
			"ArtifactLibA",
			"using ArtifactLibB; namespace ArtifactLibA; public int Combined() { return BaseValue() + 2; }",
			libB);

		var appDirectory = Path.Combine(_root, "ArtifactApp");
		Directory.CreateDirectory(appDirectory);
		WriteProject(Path.Combine(appDirectory, "App.cvlproj"), "App", libA);
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), "using ArtifactLibA; int main() { return Combined() - 42; }");

		var graph = ProjectBuildGraph.Load(appDirectory);
		const string buildKey = "build-v2|configuration=Debug|project-artifacts-test";
		var plan = ProjectBuildPlan.Create(graph, buildKey);
		var prepared = ProjectReferenceBuildPipeline.Prepare(graph, plan, buildKey);

		Assert.True(prepared.UseArtifacts);
		Assert.Equal(2, prepared.BuiltProjects);
		Assert.Equal(2, prepared.ArtifactPaths.Count);
		Assert.All(prepared.ArtifactPaths, path => Assert.True(File.Exists(path), path));

		var secondPlan = ProjectBuildPlan.Create(ProjectBuildGraph.Load(appDirectory), buildKey);
		var reused = ProjectReferenceBuildPipeline.Prepare(ProjectBuildGraph.Load(appDirectory), secondPlan, buildKey);
		Assert.Equal(0, reused.BuiltProjects);
		Assert.Equal(2, reused.ReusedProjects);

		// Remove referenced sources after producing their artifacts. The consumer must still
		// compile and link, proving it is not silently falling back to source merging.
		File.Delete(Path.Combine(Path.GetDirectoryName(libA)!, "ArtifactLibA.cvl"));
		File.Delete(Path.Combine(Path.GetDirectoryName(libB)!, "ArtifactLibB.cvl"));

		var cache = new PackageCache(Path.Combine(_root, "artifact-cache"));
		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, buildOutput) = CompileWithCapturedOutput(driver, appDirectory, useProjectReferencePackages: true);
		Assert.True(exitCode == 0, buildOutput);

		var executable = Path.Combine(appDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "App.exe" : "App");
		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		});
		Assert.NotNull(process);
		Assert.True(process!.WaitForExit(10_000));
		Assert.Equal(0, process.ExitCode);
	}

	[Fact]
	public void CompilationProject_DiamondProjectReference_DeduplicatesSharedDependency()
	{
		var commonDirectory = CreateLibrary("Common", "namespace Common; public int Value() { return 42; }");
		var leftDirectory = CreateLibrary("Left", "namespace Left; public int LeftValue() { return 1; }");
		var rightDirectory = CreateLibrary("Right", "namespace Right; public int RightValue() { return 1; }");
		AddProjectReference(leftDirectory, Path.Combine(commonDirectory, "Common.cvlproj"));
		AddProjectReference(rightDirectory, Path.Combine(commonDirectory, "Common.cvlproj"));

		var appDirectory = Path.Combine(_root, "DiamondApp");
		Directory.CreateDirectory(appDirectory);
		WriteProject(Path.Combine(appDirectory, "DiamondApp.cvlproj"), "DiamondApp",
			Path.Combine(leftDirectory, "Left.cvlproj"), Path.Combine(rightDirectory, "Right.cvlproj"));
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), "int main() { return 0; }");

		var project = CompilationProject.Load(appDirectory, compilerBaseDir: _root);
		var commonProject = Path.GetFullPath(Path.Combine(commonDirectory, "Common.cvlproj"));
		var commonSource = Path.GetFullPath(Path.Combine(commonDirectory, "Common.cvl"));

		Assert.Equal(1, project.ProjectReferences.Count(path => string.Equals(path, commonProject, StringComparison.OrdinalIgnoreCase)));
		Assert.Equal(1, project.SourceFiles.Count(path => string.Equals(path, commonSource, StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public void CompilationProject_ProjectReferenceCycle_FailsWithCycleChain()
	{
		var aDirectory = CreateLibrary("A", "namespace A; public int AValue() { return 1; }");
		var bDirectory = CreateLibrary("B", "namespace B; public int BValue() { return 2; }");
		AddProjectReference(aDirectory, Path.Combine(bDirectory, "B.cvlproj"));
		AddProjectReference(bDirectory, Path.Combine(aDirectory, "A.cvlproj"));

		var ex = Assert.Throws<InvalidOperationException>(() =>
			CompilationProject.Load(Path.Combine(aDirectory, "A.cvlproj"), compilerBaseDir: _root));

		Assert.Contains("ProjectReference cycle detected", ex.Message);
		Assert.Contains("A -> B -> A", ex.Message);
	}

	[Fact]
	public void CompilationProject_MissingProjectReference_FailsWithReferencedPath()
	{
		var appDirectory = Path.Combine(_root, "missing-app");
		Directory.CreateDirectory(appDirectory);
		File.WriteAllText(Path.Combine(appDirectory, "App.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Missing\Missing.cvlproj" />
  </ItemGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), "int main() { return 0; }");

		var ex = Assert.Throws<FileNotFoundException>(() => CompilationProject.Load(appDirectory, compilerBaseDir: _root));
		Assert.Contains("ProjectReference", ex.Message);
		Assert.Contains("Missing.cvlproj", ex.Message);
	}

	private (string AppDirectory, string LibAProject, string LibBProject, string LibBSource) CreateTransitiveProjectGraph()
	{
		var libBDirectory = CreateLibrary("LibB", "namespace LibB; public int BaseValue() { return 40; }");
		var libADirectory = CreateLibrary("LibA", "using LibB; namespace LibA; public int Combined() { return BaseValue() + 2; }");
		AddProjectReference(libADirectory, Path.Combine(libBDirectory, "LibB.cvlproj"));

		var appDirectory = Path.Combine(_root, "TransitiveApp");
		Directory.CreateDirectory(appDirectory);
		WriteProject(Path.Combine(appDirectory, "App.cvlproj"), "App", Path.Combine(libADirectory, "LibA.cvlproj"));
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), "using LibA; int main() { return Combined() - 42; }");

		return (
			appDirectory,
			Path.Combine(libADirectory, "LibA.cvlproj"),
			Path.Combine(libBDirectory, "LibB.cvlproj"),
			Path.Combine(libBDirectory, "LibB.cvl"));
	}

	private string CreatePackageReadyLibrary(string name, string source, params string[] projectReferences)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		var projectPath = Path.Combine(directory, name + ".cvlproj");
		var references = projectReferences.Length == 0
			? string.Empty
			: "\n  <ItemGroup>\n" + string.Join("\n", projectReferences.Select(reference =>
				$"    <ProjectReference Include=\"{Path.GetRelativePath(directory, reference)}\" />")) + "\n  </ItemGroup>";
		File.WriteAllText(projectPath, $"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>{name}</AssemblyName>
    <PackageId>{name}</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>{references}
</Project>
""");
		File.WriteAllText(Path.Combine(directory, name + ".cvl"), source);
		return projectPath;
	}

	private string CreateLibrary(string name, string source)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		WriteProject(Path.Combine(directory, $"{name}.cvlproj"), name);
		File.WriteAllText(Path.Combine(directory, $"{name}.cvl"), source);
		return directory;
	}

	private static void AddProjectReference(string projectDirectory, string referencedProject)
	{
		var projectPath = Directory.GetFiles(projectDirectory, "*.cvlproj", SearchOption.TopDirectoryOnly).Single();
		var projectName = Path.GetFileNameWithoutExtension(projectPath);
		WriteProject(projectPath, projectName, referencedProject);
	}

	private static void WriteProject(string projectPath, string assemblyName, params string[] projectReferences)
	{
		var projectDirectory = Path.GetDirectoryName(projectPath)!;
		var references = projectReferences.Length == 0
			? string.Empty
			: "\n  <ItemGroup>\n" + string.Join("\n", projectReferences.Select(reference =>
				$"    <ProjectReference Include=\"{Path.GetRelativePath(projectDirectory, reference)}\" />")) + "\n  </ItemGroup>";

		File.WriteAllText(projectPath, $"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>{(assemblyName == "App" || assemblyName == "DiamondApp" ? "Exe" : "Library")}</OutputType>
    <AssemblyName>{assemblyName}</AssemblyName>
  </PropertyGroup>{references}
</Project>
""");
	}

	private (string AppDirectory, string LibraryProject, string LibrarySource) CreateProjectPair()
	{
		var libraryDirectory = Path.Combine(_root, "MathLib");
		var appDirectory = Path.Combine(_root, "App");
		Directory.CreateDirectory(libraryDirectory);
		Directory.CreateDirectory(appDirectory);

		var libraryProject = Path.Combine(libraryDirectory, "MathLib.cvlproj");
		File.WriteAllText(libraryProject, """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>MathLib</AssemblyName>
  </PropertyGroup>
</Project>
""");

		var librarySource = Path.Combine(libraryDirectory, "MathLib.cvl");
		File.WriteAllText(librarySource, """
namespace MathLib;

public struct Pair {
    public int X;
    public int Y;
}

public int Sum(Pair value) {
    return value.X + value.Y;
}

public struct Box<T> {
    public T Value;
}

public T Identity<T>(T value) {
    return value;
}
""");

		File.WriteAllText(Path.Combine(appDirectory, "App.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\MathLib\MathLib.cvlproj" />
  </ItemGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), """
using MathLib;

int main() {
    Pair pair = Pair { X: 20, Y: 22 };
    Box<int> box = Box<int> { Value: Sum(pair) };
    return Identity<int>(box.Value) - 42;
}
""");

		return (appDirectory, libraryProject, librarySource);
	}

	private static (int ExitCode, string Output) CompileWithCapturedOutput(ICompilerDriver driver, string path, bool useProjectReferencePackages = false)
	{
		var originalOut = Console.Out;
		var originalError = Console.Error;
		using var output = new StringWriter();
		using var error = new StringWriter();
		try
		{
			Console.SetOut(output);
			Console.SetError(error);
			var exitCode = driver.Compile(path, llvmOnly: false, isShared: false, emitIr: false, optLevel: "O0", verbose: true, useProjectReferencePackages: useProjectReferencePackages);
			return (exitCode, output.ToString() + error.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	private static void RequireClang()
	{
		if (FindClang() is null)
			Assert.Skip("clang not available; project-reference build integration requires clang/LLVM.");
	}

	private static string? FindClang()
	{
		var local = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "clang.exe" : "clang");
		if (File.Exists(local))
			return local;

		var executable = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
		foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			var candidate = Path.Combine(directory, executable);
			if (File.Exists(candidate))
				return candidate;
		}
		return null;
	}

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}
}
