using System.Buffers.Binary;
using System.Text.Json;
using Cvolo.CLI.Packages;
using Cvolo.Core.Packages;
using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

[Collection("Sequential")]
public sealed class LocalPackageManagerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-local-pkg-" + Guid.NewGuid().ToString("N"));

	public LocalPackageManagerTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void Resolve_FloatingVersion_PicksHighestMatchingVersionAndTransitives()
	{
		var feedDir = Dir("feed");
		CreateArchive(feedDir, "Foo", "1.0.0");
		CreateArchive(feedDir, "Foo", "1.2.0", [new PackageReference("Bar", "^2.0.0")]);
		CreateArchive(feedDir, "Foo", "2.0.0");
		CreateArchive(feedDir, "Bar", "2.3.0");
		var manifest = CreateProject("app", feedDir, [new PackageReference("Foo", "1.*")]);

		var graph = new DependencyResolver().Resolve(manifest, [LocalFeed.Load(feedDir, manifest.ProjectDirectory)]);

		Assert.Equal("1.2.0", graph.Packages["Foo"].Version);
		Assert.Equal("2.3.0", graph.Packages["Bar"].Version);
	}

	[Fact]
	public void Resolve_IncompatibleTransitiveRanges_ThrowsCVLP3020()
	{
		var feedDir = Dir("feed");
		CreateArchive(feedDir, "A", "1.0.0", [new PackageReference("C", "^1.0.0")]);
		CreateArchive(feedDir, "B", "1.0.0", [new PackageReference("C", "^2.0.0")]);
		CreateArchive(feedDir, "C", "1.5.0");
		var manifest = CreateProject("app", feedDir, [new PackageReference("A", "1.0.0"), new PackageReference("B", "1.0.0")]);

		var ex = Assert.Throws<PackageException>(() => new DependencyResolver().Resolve(manifest, [LocalFeed.Load(feedDir, manifest.ProjectDirectory)]));

		Assert.Equal(PackageDiagnosticIds.VersionConflict, ex.Code);
	}

	[Fact]
	public void LockFile_WritesDeterministicSortedJson()
	{
		var lockPath = Path.Combine(_root, "cvolo.lock.json");
		var lockFile = new LockFile
		{
			Sources = ["file://z", "file://a"],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Zoo"] = new("1.0.0", "blake3:z", new Dictionary<string, string>()),
				["Alpha"] = new("1.0.0", "blake3:a", new Dictionary<string, string> { ["Beta"] = "^1.0.0" })
			}
		};

		lockFile.Write(lockPath);
		var first = File.ReadAllText(lockPath);
		lockFile.Write(lockPath);
		var second = File.ReadAllText(lockPath);

		Assert.Equal(first, second);
		Assert.True(first.IndexOf("file://a", StringComparison.Ordinal) < first.IndexOf("file://z", StringComparison.Ordinal));
		Assert.True(first.IndexOf("Alpha", StringComparison.Ordinal) < first.IndexOf("Zoo", StringComparison.Ordinal));
	}

	[Fact]
	public void ProjectManifestWriter_AddsUpdatesAndRemovesPackageReference()
	{
		var manifest = CreateProject("edit", Dir("feed"), []);

		ProjectManifestWriter.AddPackageReference(manifest, "Foo", "^1.0.0");
		manifest = ProjectManifest.Load(manifest.ProjectPath);
		Assert.Equal("^1.0.0", Assert.Single(manifest.Dependencies).Version);

		ProjectManifestWriter.AddPackageReference(manifest, "Foo", "~1.2.0");
		manifest = ProjectManifest.Load(manifest.ProjectPath);
		Assert.Equal("~1.2.0", Assert.Single(manifest.Dependencies).Version);

		Assert.True(ProjectManifestWriter.RemovePackageReference(manifest, "Foo"));
		manifest = ProjectManifest.Load(manifest.ProjectPath);
		Assert.Empty(manifest.Dependencies);
	}

	[Fact]
	public void LocalFeed_IndexRejectsMismatchedHash()
	{
		var feedDir = Dir("indexed-feed");
		CreateArchive(feedDir, "Foo", "1.0.0");
		WriteFeedIndex(feedDir, "Foo", "1.0.0", "blake3:bad", "Foo.1.0.0.cvlib");

		var ex = Assert.Throws<PackageException>(() => LocalFeed.Load(feedDir, _root));

		Assert.Equal(PackageDiagnosticIds.CachedContentMismatch, ex.Code);
	}

	[Fact]
	public void LocalFeed_IndexRejectsMismatchedIdentity()
	{
		var feedDir = Dir("identity-feed");
		var archive = CreateArchive(feedDir, "Foo", "1.0.0");
		WriteFeedIndex(feedDir, "Bar", "1.0.0", LocalFeed.ComputeHash(archive), "Foo.1.0.0.cvlib");

		var ex = Assert.Throws<PackageException>(() => LocalFeed.Load(feedDir, _root));

		Assert.Equal(PackageDiagnosticIds.PackageIdMismatch, ex.Code);
	}

	[Fact]
	public void PkgUpdate_UnknownPackageId_ReportsNotReferenced()
	{
		// TODO: inject working directory and console writers into PkgCommand so CLI tests do not mutate process-wide state.
		var feedDir = Dir("update-feed");
		CreateArchive(feedDir, "Foo", "1.0.0");
		CreateProject("update-app", feedDir, [new PackageReference("Foo", "1.0.0")]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		var command = new PkgCommand(cache, new PackageInstaller(cache));
		var previousCurrentDirectory = Directory.GetCurrentDirectory();
		var previousError = Console.Error;
		using var error = new StringWriter();
		try
		{
			Directory.SetCurrentDirectory(Path.Combine(_root, "update-app"));
			Console.SetError(error);

			var exitCode = command.Parse("update Missing").Invoke();

			Assert.Equal(1, exitCode);
			Assert.Contains(PackageDiagnosticIds.PackageNotReferenced, error.ToString());
		}
		finally
		{
			Console.SetError(previousError);
			Directory.SetCurrentDirectory(previousCurrentDirectory);
		}
	}

	[Fact]
	public void PkgCachePruneUnused_RemovesOnlyPackagesNotReferencedByLocks()
	{
		// TODO: inject working directory and console writers into PkgCommand so CLI tests do not mutate process-wide state.
		var projectDir = Dir("prune-app");
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		WriteCachedPackage(cache, "Foo", "1.0.0");
		WriteCachedPackage(cache, "Bar", "2.0.0");
		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("1.0.0", "blake3:foo", new Dictionary<string, string>())
			}
		}.Write(Path.Combine(projectDir, "cvolo.lock.json"));
		var command = new PkgCommand(cache, new PackageInstaller(cache));
		var previousCurrentDirectory = Directory.GetCurrentDirectory();
		var previousOutput = Console.Out;
		using var output = new StringWriter();
		try
		{
			Directory.SetCurrentDirectory(projectDir);
			Console.SetOut(output);

			var exitCode = command.Parse("cache prune --unused").Invoke();

			Assert.Equal(0, exitCode);
			Assert.True(Directory.Exists(cache.GetPackageDirectory("Foo", "1.0.0")));
			Assert.False(Directory.Exists(cache.GetPackageDirectory("Bar", "2.0.0")));
			Assert.Contains("Removed 1 cached package", output.ToString());
		}
		finally
		{
			Console.SetOut(previousOutput);
			Directory.SetCurrentDirectory(previousCurrentDirectory);
		}
	}

	private string Dir(string name)
	{
		var path = Path.Combine(_root, name);
		Directory.CreateDirectory(path);
		return path;
	}

	private ProjectManifest CreateProject(string name, string feedDir, IReadOnlyList<PackageReference> dependencies)
	{
		var dir = Dir(name);
		var references = string.Join(Environment.NewLine, dependencies.Select(d => $"    <PackageReference Include=\"{d.Id}\" Version=\"{d.Version}\" />"));
		var itemGroup = dependencies.Count == 0 ? string.Empty : $"\n  <ItemGroup>\n{references}\n  </ItemGroup>";
		File.WriteAllText(Path.Combine(dir, "App.cvlproj"), $$"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <PackageId>App</PackageId>
    <Version>1.0.0</Version>
    <LocalFeed>{{feedDir}}</LocalFeed>
  </PropertyGroup>{{itemGroup}}
</Project>
""");
		return ProjectManifest.Load(dir);
	}

	private static string CreateArchive(string feedDir, string id, string version, IReadOnlyList<PackageReference>? dependencies = null)
	{
		var path = Path.Combine(feedDir, $"{id}.{version}.cvlib");
		var slices = new[] { new CvlSliceEntry(TargetTriple.HostTriple(), new(0, 1), new(0, 1)) };
		var json = JsonSerializer.SerializeToUtf8Bytes(new
		{
			Format = "cvlib.slice-manifest.v1",
			PackageId = id,
			Version = version,
			Dependencies = dependencies ?? [],
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
		writer.WriteUnsigned(path);
		return path;
	}

	private static void WriteFeedIndex(string feedDir, string id, string version, string hash, string file)
	{
		File.WriteAllText(Path.Combine(feedDir, "index.json"), JsonSerializer.Serialize(new
		{
			Format = "cvolo.feed.v1",
			Packages = new[] { new { Id = id, Version = version, Hash = hash, File = file } }
		}));
	}

	private static void WriteCachedPackage(PackageCache cache, string id, string version)
	{
		var directory = cache.GetPackageDirectory(id, version);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, ".metadata.json"), JsonSerializer.Serialize(new InstalledPackageMetadata(id, version, id + ".cvlib", "blake3:" + id.ToLowerInvariant(), DateTimeOffset.UtcNow)));
	}

	public void Dispose() => Directory.Delete(_root, true);
}
