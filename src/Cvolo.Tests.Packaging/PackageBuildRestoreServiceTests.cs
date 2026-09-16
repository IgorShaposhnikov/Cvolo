using System.Buffers.Binary;
using System.Text.Json;
using Cvolo.Core.Packages;
using Cvolo.Packaging;
using NSec.Cryptography;

namespace Cvolo.Tests.Packaging;

public sealed class PackageBuildRestoreServiceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-build-restore-" + Guid.NewGuid().ToString("N"));

	public PackageBuildRestoreServiceTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void RestoreIfRequired_PackageProjectWithMissingLock_RestoresBeforeBuild()
	{
		var feed = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;
		CreateArchive(feed, "Foo", "1.2.0");
		var project = CreatePackageProject("app", feed, includeReference: true);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		var installer = new PackageInstaller(cache);
		var service = new PackageBuildRestoreService(new PackageRestoreService(installer));

		var result = service.RestoreIfRequired(project, noRestore: false);

		Assert.NotNull(result);
		Assert.True(result.LockFileUpdated);
		Assert.True(File.Exists(Path.Combine(project, "cvolo.lock.json")));
		Assert.True(installer.IsInstalled("Foo", "1.2.0"));
	}

	[Fact]
	public void RestoreIfRequired_NoRestore_LeavesMissingLockAndCacheUntouched()
	{
		var feed = Directory.CreateDirectory(Path.Combine(_root, "feed-no-restore")).FullName;
		CreateArchive(feed, "Foo", "1.2.0");
		var project = CreatePackageProject("app-no-restore", feed, includeReference: true);
		var cache = new PackageCache(Path.Combine(_root, "cache-no-restore"));
		var installer = new PackageInstaller(cache);
		var service = new PackageBuildRestoreService(new PackageRestoreService(installer));

		var result = service.RestoreIfRequired(project, noRestore: true);

		Assert.Null(result);
		Assert.False(File.Exists(Path.Combine(project, "cvolo.lock.json")));
		Assert.False(installer.IsInstalled("Foo", "1.2.0"));
	}

	[Fact]
	public void RestoreIfRequired_ProjectWithoutPackageReferences_DoesNotRequirePackageIdentity()
	{
		var project = Path.Combine(_root, "plain-app");
		Directory.CreateDirectory(project);
		File.WriteAllText(Path.Combine(project, "Plain.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>Plain</AssemblyName>
  </PropertyGroup>
</Project>
""");
		var cache = new PackageCache(Path.Combine(_root, "cache-plain"));
		var service = new PackageBuildRestoreService(new PackageRestoreService(new PackageInstaller(cache)));

		var result = service.RestoreIfRequired(project, noRestore: false);

		Assert.Null(result);
		Assert.False(File.Exists(Path.Combine(project, "cvolo.lock.json")));
	}

	[Fact]
	public void RestoreIfRequired_DirectSourceFile_DoesNotRestoreContainingProject()
	{
		var feed = Directory.CreateDirectory(Path.Combine(_root, "feed-source")).FullName;
		CreateArchive(feed, "Foo", "1.2.0");
		var project = CreatePackageProject("source-app", feed, includeReference: true);
		var source = Path.Combine(project, "Main.cvl");
		File.WriteAllText(source, "int main() { return 0; }");
		var cache = new PackageCache(Path.Combine(_root, "cache-source"));
		var service = new PackageBuildRestoreService(new PackageRestoreService(new PackageInstaller(cache)));

		var result = service.RestoreIfRequired(source, noRestore: false);

		Assert.Null(result);
		Assert.False(File.Exists(Path.Combine(project, "cvolo.lock.json")));
	}

	private string CreatePackageProject(string name, string feed, bool includeReference)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		var reference = includeReference ? "\n  <ItemGroup>\n    <PackageReference Include=\"Foo\" Version=\"1.*\" />\n  </ItemGroup>" : string.Empty;
		File.WriteAllText(Path.Combine(directory, "App.cvlproj"), $$"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <PackageId>App</PackageId>
    <Version>1.0.0</Version>
    <LocalFeed>{{feed}}</LocalFeed>
  </PropertyGroup>{{reference}}
</Project>
""");
		return directory;
	}

	private static void CreateArchive(string feed, string id, string version)
	{
		var path = Path.Combine(feed, $"{id}.{version}.cvlib");
		var slices = new[] { new CvlSliceEntry(TargetTriple.HostTriple(), new(0, 1), new(0, 1)) };
		var json = JsonSerializer.SerializeToUtf8Bytes(new
		{
			Format = "cvlib.slice-manifest.v1",
			PackageId = id,
			Version = version,
			Dependencies = Array.Empty<PackageReference>(),
			Slices = slices
		});
		var sector = new byte[json.Length + 6];
		BinaryPrimitives.WriteUInt32LittleEndian(sector, (uint)json.Length);
		json.CopyTo(sector, 4);
		"{}"u8.CopyTo(sector.AsSpan(4 + json.Length));

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, sector);
		writer.SetSector(2, new byte[] { 1 });
		writer.SetSector(3, new byte[] { 2 });
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		writer.Write(path, key);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}
}
