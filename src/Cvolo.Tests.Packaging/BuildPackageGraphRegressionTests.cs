using System.Diagnostics;
using Cvolo.Drivers;
using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

/// <summary>
/// Cross-feature regression coverage for the build/package graph. These tests intentionally
/// combine systems that already have focused unit coverage: ProjectReference artifacts,
/// package dependencies, graph invalidation, configurations, and clean behavior.
/// </summary>
public sealed class BuildPackageGraphRegressionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-build-package-graph-" + Guid.NewGuid().ToString("N"));

	public BuildPackageGraphRegressionTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void Build_AppWithPackageAndProjectReference_LinksBothDependencyKinds()
	{
		RequireClang();

		var packagePath = CreatePackedPackage(
			"FeedMath",
			"namespace FeedMath; public int PackageValue() { return 20; }");
		var cache = new PackageCache(Path.Combine(_root, "mixed-cache"));
		new PackageInstaller(cache).InstallFromFile(packagePath);

		var projectLibrary = CreatePackageReadyLibrary(
			"ProjectMath",
			"namespace ProjectMath; public int ProjectValue() { return 22; }");
		var appDirectory = CreateApp(
			"MixedApp",
			"using FeedMath; using ProjectMath; int main() { return PackageValue() + ProjectValue() - 42; }",
			[projectLibrary],
			[new PackageReference("FeedMath", "1.0.0")]);

		WriteLock(appDirectory, "FeedMath", "1.0.0", LocalFeed.ComputeHash(packagePath));

		const string buildKey = "build-v40|configuration=Debug|mixed-dependencies";
		var graph = ProjectBuildGraph.Load(appDirectory);
		var plan = ProjectBuildPlan.Create(graph, buildKey);
		var projectReferences = ProjectReferenceBuildPipeline.Prepare(graph, plan, buildKey);

		Assert.True(projectReferences.UseArtifacts);
		Assert.Equal(1, projectReferences.BuiltProjects);
		Assert.Single(projectReferences.ArtifactPaths);

		var driver = CreateDriver(cache);
		var (exitCode, output) = Compile(driver, appDirectory, useProjectReferencePackages: true);
		Assert.True(exitCode == 0, output);
		AssertExecutableReturns(appDirectory, "MixedApp", 0);
	}

	[Fact]
	public void Build_DiamondProjectGraph_LinksSharedDependencyExactlyOnce()
	{
		RequireClang();

		var common = CreatePackageReadyLibrary(
			"DiamondCommon",
			"namespace DiamondCommon; public global int Bias = 2; public int BaseValue() { return 10; }");
		var left = CreatePackageReadyLibrary(
			"DiamondLeft",
			"using DiamondCommon; namespace DiamondLeft; public int LeftValue() { return BaseValue() + 1; }",
			common);
		var right = CreatePackageReadyLibrary(
			"DiamondRight",
			"using DiamondCommon; namespace DiamondRight; public int RightValue() { return Bias + 1; }",
			common);
		var appDirectory = CreateApp(
			"DiamondApp",
			"using DiamondCommon; using DiamondLeft; using DiamondRight; int main() { return LeftValue() + RightValue() + BaseValue() + Bias - 26; }",
			[left, right],
			[]);

		const string buildKey = "build-v40|configuration=Debug|diamond-artifacts";
		var graph = ProjectBuildGraph.Load(appDirectory);
		var firstPlan = ProjectBuildPlan.Create(graph, buildKey);
		var first = ProjectReferenceBuildPipeline.Prepare(graph, firstPlan, buildKey);

		Assert.True(first.UseArtifacts);
		Assert.Equal(3, first.BuiltProjects);
		Assert.Equal(3, first.ArtifactPaths.Count);
		Assert.Equal(3, first.ArtifactPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

		var secondGraph = ProjectBuildGraph.Load(appDirectory);
		var secondPlan = ProjectBuildPlan.Create(secondGraph, buildKey);
		var second = ProjectReferenceBuildPipeline.Prepare(secondGraph, secondPlan, buildKey);
		Assert.Equal(0, second.BuiltProjects);
		Assert.Equal(3, second.ReusedProjects);

		var driver = CreateDriver(new PackageCache(Path.Combine(_root, "diamond-cache")));
		var (exitCode, output) = Compile(driver, appDirectory, useProjectReferencePackages: true);
		Assert.True(exitCode == 0, output);
		AssertExecutableReturns(appDirectory, "DiamondApp", 0);
	}

	[Fact]
	public void BuildPlan_ChangingOneDiamondBranch_InvalidatesOnlyBranchAndConsumer()
	{
		var common = CreateProject("PlanCommon", "namespace PlanCommon; public int Value() { return 1; }");
		var left = CreateProject("PlanLeft", "namespace PlanLeft; public int Left() { return 1; }", common);
		var right = CreateProject("PlanRight", "namespace PlanRight; public int Right() { return 1; }", common);
		var app = CreateProject("PlanApp", "int main() { return 0; }", left, right);
		const string buildKey = "build-v40|configuration=Debug|diamond-plan";

		var initial = ProjectBuildGraph.Load(app);
		ProjectBuildPlan.RecordSuccessful(initial, buildKey);

		File.WriteAllText(
			Path.Combine(Path.GetDirectoryName(left)!, "PlanLeft.cvl"),
			"namespace PlanLeft; public int Left() { return 2; }");

		var plan = ProjectBuildPlan.Create(ProjectBuildGraph.Load(app), buildKey);
		var byName = plan.Nodes.ToDictionary(
			node => Path.GetFileNameWithoutExtension(node.Project.ProjectPath),
			StringComparer.OrdinalIgnoreCase);

		Assert.False(byName["PlanCommon"].RequiresBuild);
		Assert.True(byName["PlanLeft"].InputsChanged);
		Assert.False(byName["PlanRight"].RequiresBuild);
		Assert.True(byName["PlanApp"].DependencyChanged);
		Assert.True(byName["PlanApp"].RequiresBuild);
	}

	[Fact]
	public void Clean_DebugRoot_PreservesReferencedArtifactsAndReleaseState()
	{
		var library = CreateProject("CleanLibrary", "namespace CleanLibrary; public int Value() { return 1; }");
		var app = CreateProject("CleanApp", "int main() { return 0; }", library);
		var appDirectory = Path.GetDirectoryName(app)!;
		var libraryDirectory = Path.GetDirectoryName(library)!;
		const string buildKey = "build-v40|configuration-state";

		var graph = ProjectBuildGraph.Load(app);
		ProjectBuildPlan.RecordSuccessful(graph, buildKey, "Debug");
		ProjectBuildPlan.RecordSuccessful(graph, buildKey, "Release");

		var appDebugArtifact = CreateMarker(Path.Combine(appDirectory, "bin", "Debug", "app.marker"));
		var appReleaseArtifact = CreateMarker(Path.Combine(appDirectory, "bin", "Release", "app.marker"));
		var libraryDebugArtifact = CreateMarker(Path.Combine(libraryDirectory, "bin", "Debug", "library.marker"));
		var libraryReleaseArtifact = CreateMarker(Path.Combine(libraryDirectory, "bin", "Release", "library.marker"));
		var libraryDebugState = Path.Combine(libraryDirectory, "obj", "Debug", "cvolo.project-state.json");
		var libraryReleaseState = Path.Combine(libraryDirectory, "obj", "Release", "cvolo.project-state.json");

		Assert.True(File.Exists(libraryDebugState));
		Assert.True(File.Exists(libraryReleaseState));

		BuildCleaner.Clean(appDirectory, "Debug");

		Assert.False(File.Exists(appDebugArtifact));
		Assert.True(File.Exists(appReleaseArtifact));
		Assert.True(File.Exists(libraryDebugArtifact));
		Assert.True(File.Exists(libraryReleaseArtifact));
		Assert.True(File.Exists(libraryDebugState));
		Assert.True(File.Exists(libraryReleaseState));
	}

	private string CreatePackedPackage(string id, string source)
	{
		var directory = Path.Combine(_root, id + "-package");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, id + ".cvlproj"), $"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>{id}</AssemblyName>
    <PackageId>{id}</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(directory, id + ".cvl"), source);

		var keyPath = Path.Combine(_root, id + ".key");
		File.WriteAllBytes(keyPath, Enumerable.Range(0, 32).Select(value => (byte)(value + id.Length)).ToArray());
		var packagePath = Path.Combine(_root, id + ".1.0.0.cvlib");
		PackPipeline.Execute(directory, new PackOptions
		{
			OutputPath = packagePath,
			SigningKeyPath = keyPath,
			Targets = [TargetTriple.HostTriple()],
			StripSource = true
		});
		return packagePath;
	}

	private string CreatePackageReadyLibrary(string name, string source, params string[] references)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		var projectPath = Path.Combine(directory, name + ".cvlproj");
		var referenceXml = references.Length == 0
			? string.Empty
			: "\n  <ItemGroup>\n" + string.Join("\n", references.Select(reference =>
				$"    <ProjectReference Include=\"{Path.GetRelativePath(directory, reference)}\" />")) + "\n  </ItemGroup>";
		File.WriteAllText(projectPath, $"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>{name}</AssemblyName>
    <PackageId>{name}</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>{referenceXml}
</Project>
""");
		File.WriteAllText(Path.Combine(directory, name + ".cvl"), source);
		return projectPath;
	}

	private string CreateApp(
		string name,
		string source,
		IReadOnlyList<string> projectReferences,
		IReadOnlyList<PackageReference> packageReferences)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		var items = projectReferences.Select(reference =>
			$"    <ProjectReference Include=\"{Path.GetRelativePath(directory, reference)}\" />")
			.Concat(packageReferences.Select(reference =>
				$"    <PackageReference Include=\"{reference.Id}\" Version=\"{reference.Version}\" />"))
			.ToArray();
		var itemGroup = items.Length == 0 ? string.Empty : "\n  <ItemGroup>\n" + string.Join("\n", items) + "\n  </ItemGroup>";
		File.WriteAllText(Path.Combine(directory, name + ".cvlproj"), $"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>{name}</AssemblyName>
    <PackageId>{name}</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>{itemGroup}
</Project>
""");
		File.WriteAllText(Path.Combine(directory, "Main.cvl"), source);
		return directory;
	}

	private string CreateProject(string name, string source, params string[] references)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		var projectPath = Path.Combine(directory, name + ".cvlproj");
		var referenceXml = references.Length == 0
			? string.Empty
			: "\n  <ItemGroup>\n" + string.Join("\n", references.Select(reference =>
				$"    <ProjectReference Include=\"{Path.GetRelativePath(directory, reference)}\" />")) + "\n  </ItemGroup>";
		File.WriteAllText(projectPath, $"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>{(name.EndsWith("App", StringComparison.Ordinal) ? "Exe" : "Library")}</OutputType>
    <AssemblyName>{name}</AssemblyName>
  </PropertyGroup>{referenceXml}
</Project>
""");
		File.WriteAllText(Path.Combine(directory, name + ".cvl"), source);
		return projectPath;
	}

	private static void WriteLock(string projectDirectory, string id, string version, string contentHash)
	{
		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				[id] = new(version, contentHash, new Dictionary<string, string>())
			}
		}.Write(Path.Combine(projectDirectory, "cvolo.lock.json"));
	}

	private static ICompilerDriver CreateDriver(PackageCache cache)
	{
		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		return Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
	}

	private static (int ExitCode, string Output) Compile(ICompilerDriver driver, string path, bool useProjectReferencePackages)
	{
		var originalOut = Console.Out;
		var originalError = Console.Error;
		using var output = new StringWriter();
		using var error = new StringWriter();
		try
		{
			Console.SetOut(output);
			Console.SetError(error);
			var exitCode = driver.Compile(
				path,
				llvmOnly: false,
				isShared: false,
				emitIr: false,
				optLevel: "O0",
				verbose: true,
				useProjectReferencePackages: useProjectReferencePackages);
			return (exitCode, output.ToString() + error.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	private static void AssertExecutableReturns(string projectDirectory, string assemblyName, int expectedExitCode)
	{
		var executable = Path.Combine(projectDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? assemblyName + ".exe" : assemblyName);
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
		var stderr = process.StandardError.ReadToEnd();
		Assert.True(process.ExitCode == expectedExitCode, $"Expected exit code {expectedExitCode}, got {process.ExitCode}. stderr: {stderr}");
	}

	private static string CreateMarker(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "marker");
		return path;
	}

	private static void RequireClang()
	{
		if (FindClang() is null)
			Assert.Skip("clang not available; build/package graph integration requires clang/LLVM.");
	}

	private static string? FindClang()
	{
		var local = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "clang.exe" : "clang");
		if (File.Exists(local))
			return local;

		var executable = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
		foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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
