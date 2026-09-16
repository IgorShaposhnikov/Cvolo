using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class LibraryBuildPipelineTests
{
	[Fact]
	public void GetOutputPath_UsesConfigurationTargetAndAssemblyName()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-library-layout-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			File.WriteAllText(Path.Combine(root, "MathLib.cvlproj"), """
				<Project>
				  <PropertyGroup>
				    <PackageId>Math.Package</PackageId>
				    <Version>1.0.0</Version>
				    <AssemblyName>MathLib</AssemblyName>
				    <OutputType>Library</OutputType>
				  </PropertyGroup>
				</Project>
				""");

			var manifest = ProjectManifest.Load(root);
			var path = LibraryBuildPipeline.GetOutputPath(manifest, "Debug", "x86_64-pc-windows-msvc");

			Assert.Equal(
				Path.Combine(root, "bin", "Debug", "x86_64-pc-windows-msvc", "MathLib.cvlib"),
				path);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void GetOutputPath_SanitizesTargetForDirectoryUse()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-library-layout-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			File.WriteAllText(Path.Combine(root, "Lib.cvlproj"), """
				<Project>
				  <PropertyGroup>
				    <PackageId>Lib</PackageId>
				    <Version>1.0.0</Version>
				    <OutputType>Library</OutputType>
				  </PropertyGroup>
				</Project>
				""");

			var manifest = ProjectManifest.Load(root);
			var path = LibraryBuildPipeline.GetOutputPath(manifest, "Debug", "custom:target/name");

			Assert.EndsWith(Path.Combine("bin", "Debug", "custom_target_name", "Lib.cvlib"), path);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
