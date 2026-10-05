using System.Diagnostics;
using Cvolo.Drivers;
using Cvolo.Packaging;
using Cvolo.Projects;
using Cvolo.Tests.Shared;

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
	public void CompilationProject_ProjectFileInput_IncludesNestedSources()
	{
		var appDirectory = Path.Combine(_root, "NestedSourceApp");
		var appSourceDirectory = Path.Combine(appDirectory, "App");
		Directory.CreateDirectory(appSourceDirectory);
		var projectPath = Path.Combine(appDirectory, "NestedSourceApp.cvlproj");
		var mainPath = Path.Combine(appSourceDirectory, "Main.cvl");

		File.WriteAllText(projectPath, """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>NestedSourceApp</AssemblyName>
  </PropertyGroup>
</Project>
""");
		TestCases.CopyFile("ProjectReference/NestedSource/Main.cvl", mainPath);

		var project = CompilationProject.Load(projectPath, compilerBaseDir: _root, mergeProjectReferences: false);

		Assert.Contains(project.SourceFiles, source => string.Equals(source, Path.GetFullPath(mainPath), StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Build_ProjectFileInput_FindsNestedNamespacedMain()
	{
		RequireClang();
		var appDirectory = Path.Combine(_root, "NestedMainApp");
		var appSourceDirectory = Path.Combine(appDirectory, "App");
		Directory.CreateDirectory(appSourceDirectory);

		File.WriteAllText(Path.Combine(appDirectory, "NestedMainApp.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>NestedMainApp</AssemblyName>
  </PropertyGroup>
</Project>
""");
		TestCases.CopyFile("ProjectReference/NestedMain/Main.cvl", Path.Combine(appSourceDirectory, "Main.cvl"));

		var cache = new PackageCache(Path.Combine(_root, "nested-main-cache"));
		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, buildOutput) = CompileWithCapturedOutput(driver, Path.Combine(appDirectory, "NestedMainApp.cvlproj"));

		Assert.True(exitCode == 0, buildOutput);
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
	public void Build_ProjectReferenceBitcode_ResolvesSdkUsedOnlyByPackageImplementation()
	{
		RequireClang();

		var libraryProject = CreatePackageReadyLibrary(
			"ArtifactSdkLib",
			"ProjectReference/PackageSdk/ArtifactSdkLib.cvl");

		var appDirectory = Path.Combine(_root, "ArtifactSdkApp");
		Directory.CreateDirectory(appDirectory);
		WriteProject(Path.Combine(appDirectory, "App.cvlproj"), "App", libraryProject);
		TestCases.CopyFile("ProjectReference/PackageSdk/App.cvl", Path.Combine(appDirectory, "Main.cvl"));

		var graph = ProjectBuildGraph.Load(appDirectory);
		const string buildKey = "build-v2|configuration=Debug|package-sdk-dependency-test";
		var plan = ProjectBuildPlan.Create(graph, buildKey);
		var prepared = ProjectReferenceBuildPipeline.Prepare(graph, plan, buildKey);
		Assert.True(prepared.UseArtifacts);
		Assert.Single(prepared.ArtifactPaths);

		// Prove the consumer resolves SDK dependencies from the .cvlib source bundle, not from the
		// referenced checkout. The implementation source is gone before the executable build starts.
		File.Delete(Path.Combine(Path.GetDirectoryName(libraryProject)!, "ArtifactSdkLib.cvl"));

		var artifacts = ProjectReferenceArtifactLoader.Load(appDirectory, "Debug");
		var artifact = Assert.Single(artifacts);
		var source = Assert.Single(artifact.SourceFiles!);
		Assert.Contains("System.Math.Float.ToRadians", source.Source, StringComparison.Ordinal);
		Assert.Contains("using Sdk = System", source.Source, StringComparison.Ordinal);

		var cache = new PackageCache(Path.Combine(_root, "artifact-sdk-cache"));
		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, buildOutput) = CompileWithCapturedOutput(driver, appDirectory, useProjectReferencePackages: true);
		Assert.True(exitCode == 0, buildOutput);

		var executable = Path.Combine(appDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "App.exe" : "App");
		Assert.True(File.Exists(executable), executable);
		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		});
		Assert.NotNull(process);
		var stdout = process!.StandardOutput.ReadToEnd();
		var stderr = process.StandardError.ReadToEnd();
		Assert.True(process.WaitForExit(10_000), $"Compiled app '{executable}' did not exit within 10 seconds.");
		Assert.True(process.ExitCode == 0, stderr);
		Assert.Contains("package-sdk", stdout, StringComparison.Ordinal);
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
		var libB = CreatePackageReadyLibrary("ArtifactLibB", "ProjectReference/Artifacts/ArtifactLibB.cvl");
		var libA = CreatePackageReadyLibrary(
			"ArtifactLibA",
			"ProjectReference/Artifacts/ArtifactLibA.cvl",
			libB);

		var appDirectory = Path.Combine(_root, "ArtifactApp");
		Directory.CreateDirectory(appDirectory);
		WriteProject(Path.Combine(appDirectory, "App.cvlproj"), "App", libA);
		TestCases.CopyFile("ProjectReference/Artifacts/App.cvl", Path.Combine(appDirectory, "Main.cvl"));

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
		var commonDirectory = CreateLibrary("Common", "ProjectReference/Diamond/Common.cvl");
		var leftDirectory = CreateLibrary("Left", "ProjectReference/Diamond/Left.cvl");
		var rightDirectory = CreateLibrary("Right", "ProjectReference/Diamond/Right.cvl");
		AddProjectReference(leftDirectory, Path.Combine(commonDirectory, "Common.cvlproj"));
		AddProjectReference(rightDirectory, Path.Combine(commonDirectory, "Common.cvlproj"));

		var appDirectory = Path.Combine(_root, "DiamondApp");
		Directory.CreateDirectory(appDirectory);
		WriteProject(Path.Combine(appDirectory, "DiamondApp.cvlproj"), "DiamondApp",
			Path.Combine(leftDirectory, "Left.cvlproj"), Path.Combine(rightDirectory, "Right.cvlproj"));
		TestCases.CopyFile("ProjectReference/EmptyMain.cvl", Path.Combine(appDirectory, "Main.cvl"));

		var project = CompilationProject.Load(appDirectory, compilerBaseDir: _root);
		var commonProject = Path.GetFullPath(Path.Combine(commonDirectory, "Common.cvlproj"));
		var commonSource = Path.GetFullPath(Path.Combine(commonDirectory, "Common.cvl"));

		Assert.Equal(1, project.ProjectReferences.Count(path => string.Equals(path, commonProject, StringComparison.OrdinalIgnoreCase)));
		Assert.Equal(1, project.SourceFiles.Count(path => string.Equals(path, commonSource, StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public void CompilationProject_ProjectReferenceCycle_FailsWithCycleChain()
	{
		var firstProjectDirectory = CreateLibrary("A", "ProjectReference/Cycle/FirstProject.cvl");
		var secondProjectDirectory = CreateLibrary("B", "ProjectReference/Cycle/SecondProject.cvl");
		AddProjectReference(firstProjectDirectory, Path.Combine(secondProjectDirectory, "B.cvlproj"));
		AddProjectReference(secondProjectDirectory, Path.Combine(firstProjectDirectory, "A.cvlproj"));

		var ex = Assert.Throws<InvalidOperationException>(() =>
			CompilationProject.Load(Path.Combine(firstProjectDirectory, "A.cvlproj"), compilerBaseDir: _root));

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
		TestCases.CopyFile("ProjectReference/EmptyMain.cvl", Path.Combine(appDirectory, "Main.cvl"));

		var ex = Assert.Throws<FileNotFoundException>(() => CompilationProject.Load(appDirectory, compilerBaseDir: _root));
		Assert.Contains("ProjectReference", ex.Message);
		Assert.Contains("Missing.cvlproj", ex.Message);
	}

	private (string AppDirectory, string LibAProject, string LibBProject, string LibBSource) CreateTransitiveProjectGraph()
	{
		var libBDirectory = CreateLibrary("LibB", "ProjectReference/Transitive/LibB.cvl");
		var libADirectory = CreateLibrary("LibA", "ProjectReference/Transitive/LibA.cvl");
		AddProjectReference(libADirectory, Path.Combine(libBDirectory, "LibB.cvlproj"));

		var appDirectory = Path.Combine(_root, "TransitiveApp");
		Directory.CreateDirectory(appDirectory);
		WriteProject(Path.Combine(appDirectory, "App.cvlproj"), "App", Path.Combine(libADirectory, "LibA.cvlproj"));
		TestCases.CopyFile("ProjectReference/Transitive/App.cvl", Path.Combine(appDirectory, "Main.cvl"));

		return (
			appDirectory,
			Path.Combine(libADirectory, "LibA.cvlproj"),
			Path.Combine(libBDirectory, "LibB.cvlproj"),
			Path.Combine(libBDirectory, "LibB.cvl"));
	}

	private string CreatePackageReadyLibrary(string name, string sourceFixture, params string[] projectReferences)
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
		TestCases.CopyFile(sourceFixture, Path.Combine(directory, name + ".cvl"));
		return projectPath;
	}

	private string CreateLibrary(string name, string sourceFixture)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		WriteProject(Path.Combine(directory, $"{name}.cvlproj"), name);
		TestCases.CopyFile(sourceFixture, Path.Combine(directory, $"{name}.cvl"));
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
		TestCases.CopyFile("ProjectReference/ProjectPair/MathLib.cvl", librarySource);

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
		TestCases.CopyFile("ProjectReference/ProjectPair/App.cvl", Path.Combine(appDirectory, "Main.cvl"));

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
		if (NativeToolchain.FindClang() is null)
			Assert.Skip("clang not available; project-reference build integration requires clang/LLVM.");
	}


	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}
}
