using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class BuildOutputLayoutTests
{
	[Fact]
	public void NativeArtifactsAndIntermediatesUseStableBinObjRoots()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-layout-" + Guid.NewGuid().ToString("N"));

		var native = BuildOutputLayout.GetNativeOutputPath(root, "App", shared: false);
		var ir = BuildOutputLayout.GetIntermediateIrPath(root, "App");
		var failures = BuildOutputLayout.GetCompilationFailuresDirectory(root);
		var state = BuildOutputLayout.GetBuildStatePath(root);

		Assert.Equal(Path.Combine(root, "bin", "Debug", "App" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)), native);
		Assert.Equal(Path.Combine(root, "obj", "Debug", "App.ll"), ir);
		Assert.Equal(Path.Combine(root, "obj", "Debug", "CompilationFailures"), failures);
		Assert.Equal(Path.Combine(root, "obj", "Debug", "cvolo.build-state.json"), state);
	}

	[Fact]
	public void PackageArtifactKeepsTargetBelowConfigurationDirectory()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-layout-" + Guid.NewGuid().ToString("N"));

		var path = BuildOutputLayout.GetPackageOutputPath(root, "MathLib", "custom:target/name");

		Assert.Equal(Path.Combine(root, "bin", "Debug", "custom_target_name", "MathLib.cvlib"), path);
	}

	[Theory]
	[InlineData("")]
	[InlineData("../Release")]
	public void ConfigurationMustBeSinglePathSegment(string configuration)
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-layout-" + Guid.NewGuid().ToString("N"));
		Assert.Throws<ArgumentException>(() => BuildOutputLayout.GetBinDirectory(root, configuration));
	}
}
