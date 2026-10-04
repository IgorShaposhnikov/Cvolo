using Cvolo.Projects;

namespace Cvolo.Tests.Packaging;

public sealed class CompilationProjectSdkDependencyTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"cvolo-compilation-project-sdk-tests",
		Guid.NewGuid().ToString("N"));

	public CompilationProjectSdkDependencyTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void Load_TracksBaseAndSystemSdkSourcesSeparately_WhenSystemIsReferencedThroughAlias()
	{
		var libraries = Path.Combine(_root, "libraries");
		var baseDirectory = Path.Combine(libraries, "Base");
		var consoleDirectory = Path.Combine(libraries, "System.Console");
		Directory.CreateDirectory(baseDirectory);
		Directory.CreateDirectory(consoleDirectory);

		var baseSource = Path.Combine(baseDirectory, "Core.cvl");
		var systemSource = Path.Combine(consoleDirectory, "Console.cvl");
		File.WriteAllText(baseSource, "struct BaseMarker { int Value; }");
		File.WriteAllText(systemSource, "namespace System.Console; public void Touch() { }");

		var projectDirectory = Path.Combine(_root, "App");
		Directory.CreateDirectory(projectDirectory);
		File.WriteAllText(Path.Combine(projectDirectory, "App.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
</Project>
""");
		var appSource = Path.Combine(projectDirectory, "Main.cvl");
		File.WriteAllText(appSource, """
using Sdk = System;

int Main() {
    Sdk.Console.Touch();
    return 0;
}
""");

		var project = CompilationProject.Load(projectDirectory, compilerBaseDir: _root);

		Assert.Contains(Path.GetFullPath(baseSource), project.BaseSourceFiles.Select(Path.GetFullPath));
		Assert.Contains(Path.GetFullPath(systemSource), project.SystemSourceFiles.Select(Path.GetFullPath));
		Assert.Contains("System.Console", project.RequiredSystemNamespaces);
		Assert.Contains(Path.GetFullPath(baseSource), project.SourceFiles.Select(Path.GetFullPath));
		Assert.Contains(Path.GetFullPath(systemSource), project.SourceFiles.Select(Path.GetFullPath));
		Assert.Contains(Path.GetFullPath(appSource), project.SourceFiles.Select(Path.GetFullPath));
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
		catch
		{
			// Best-effort cleanup only; assertion failures must not be hidden by temp-file cleanup.
		}
	}
}
