using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class BuildCleanerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-clean-" + Guid.NewGuid().ToString("N"));

	public BuildCleanerTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void Clean_Configuration_RemovesOnlyRequestedBinAndObjDirectories()
	{
		var debugBin = BuildOutputLayout.GetBinDirectory(_root, "Debug");
		var debugObj = BuildOutputLayout.GetObjDirectory(_root, "Debug");
		var releaseBin = BuildOutputLayout.GetBinDirectory(_root, "Release");
		var releaseObj = BuildOutputLayout.GetObjDirectory(_root, "Release");
		CreateMarker(debugBin);
		CreateMarker(debugObj);
		CreateMarker(releaseBin);
		CreateMarker(releaseObj);

		var deleted = BuildCleaner.Clean(_root, "debug");

		Assert.Equal(2, deleted.Count);
		Assert.False(Directory.Exists(debugBin));
		Assert.False(Directory.Exists(debugObj));
		Assert.True(Directory.Exists(releaseBin));
		Assert.True(Directory.Exists(releaseObj));
	}

	[Fact]
	public void Clean_WithoutConfiguration_RemovesAllBuildOutputsButKeepsPackageState()
	{
		CreateMarker(BuildOutputLayout.GetBinDirectory(_root, "Debug"));
		CreateMarker(BuildOutputLayout.GetObjDirectory(_root, "Release"));
		var packageState = Path.Combine(_root, ".cvolo", "build", "lock-hash");
		CreateMarker(packageState);

		var deleted = BuildCleaner.Clean(_root);

		Assert.Equal(2, deleted.Count);
		Assert.False(Directory.Exists(BuildOutputLayout.GetBinRoot(_root)));
		Assert.False(Directory.Exists(BuildOutputLayout.GetObjRoot(_root)));
		Assert.True(Directory.Exists(packageState));
	}

	[Fact]
	public void Clean_MissingOutputs_IsIdempotent()
	{
		var first = BuildCleaner.Clean(_root, "Release");
		var second = BuildCleaner.Clean(_root, "Release");

		Assert.Empty(first);
		Assert.Empty(second);
	}

	[Fact]
	public void Clean_RejectsUnknownConfigurationBeforeDeletingAnything()
	{
		var debugBin = BuildOutputLayout.GetBinDirectory(_root, "Debug");
		CreateMarker(debugBin);

		Assert.Throws<ArgumentException>(() => BuildCleaner.Clean(_root, "Staging"));
		Assert.True(Directory.Exists(debugBin));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	private static void CreateMarker(string directory)
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "marker.txt"), "generated");
	}
}
