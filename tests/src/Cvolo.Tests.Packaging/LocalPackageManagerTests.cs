using System.Buffers.Binary;
using System.Text.Json;
using Cvolo.CLI.Packages;
using Cvolo.Core.Packages;
using Cvolo.Packaging;
using NSec.Cryptography;

namespace Cvolo.Tests.Packaging;

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
	public void LockFile_ReadRejectsUnsupportedVersion()
	{
		var lockPath = Path.Combine(_root, "cvolo.lock.json");
		File.WriteAllText(lockPath, """
{
  "version": 2,
  "sources": [],
  "packages": {}
}
""");

		var ex = Assert.Throws<InvalidDataException>(() => LockFile.Read(lockPath));

		Assert.Contains("Unsupported", ex.Message);
	}

	[Fact]
	public void LockFile_ReadRejectsMissingPackageFields()
	{
		var lockPath = Path.Combine(_root, "cvolo.lock.json");
		File.WriteAllText(lockPath, """
{
  "version": 1,
  "sources": [],
  "packages": {
    "Foo": { "resolved": "1.0.0", "contentHash": "blake3:abc" }
  }
}
""");

		var ex = Assert.Throws<InvalidDataException>(() => LockFile.Read(lockPath));

		Assert.Contains("dependencies", ex.Message);
	}

	[Fact]
	public void LockFile_ReadNormalizesPackageLookupToIgnoreCase()
	{
		var lockPath = Path.Combine(_root, "cvolo.lock.json");
		File.WriteAllText(lockPath, $$"""
{
  "version": 1,
  "sources": [],
  "packages": {
    "Foo": { "resolved": "1.0.0", "contentHash": "{{ValidHash("foo")}}", "dependencies": { "Bar": "^1.0.0" } },
    "Bar": { "resolved": "1.2.0", "contentHash": "{{ValidHash("bar")}}", "dependencies": {} }
  }
}
""");

		var lockFile = LockFile.Read(lockPath);

		Assert.True(lockFile.Packages.ContainsKey("foo"));
		Assert.True(lockFile.Packages["foo"].Dependencies.ContainsKey("bar"));
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
		var feedDir = Dir("update-feed");
		CreateArchive(feedDir, "Foo", "1.0.0");
		var manifest = CreateProject("update-app", feedDir, [new PackageReference("Foo", "1.0.0")]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => manifest.ProjectDirectory, output, error);

		var exitCode = command.Parse("update Missing").Invoke();

		Assert.Equal(1, exitCode);
		Assert.Contains(PackageDiagnosticIds.PackageNotReferenced, error.ToString());
	}
	[Fact]
	public void PkgRemove_UnknownPackageId_ReportsNotReferenced()
	{
		var feedDir = Dir("remove-feed");
		CreateArchive(feedDir, "Foo", "1.0.0");
		var manifest = CreateProject("remove-app", feedDir, [new PackageReference("Foo", "1.0.0")]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => manifest.ProjectDirectory, output, error);

		var exitCode = command.Parse("remove Missing").Invoke();

		Assert.Equal(1, exitCode);
		Assert.Contains(PackageDiagnosticIds.PackageNotReferenced, error.ToString());
	}
	[Fact]
	public void PkgCachePruneUnused_RemovesOnlyPackagesNotReferencedByLocks()
	{
		var projectDir = Dir("prune-app");
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		WriteCachedPackage(cache, "Foo", "1.0.0");
		WriteCachedPackage(cache, "Bar", "2.0.0");
		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("1.0.0", ValidHash("foo"), new Dictionary<string, string>())
			}
		}.Write(Path.Combine(projectDir, "cvolo.lock.json"));
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => projectDir, output, error);

		var exitCode = command.Parse("cache prune --unused").Invoke();

		Assert.Equal(0, exitCode);
		Assert.True(Directory.Exists(cache.GetPackageDirectory("Foo", "1.0.0")));
		Assert.False(Directory.Exists(cache.GetPackageDirectory("Bar", "2.0.0")));
		Assert.Contains("Removed 1 cached package", output.ToString());
	}
	[Fact]
	public void PkgCachePruneUnused_RewritesPackageIndex()
	{
		var projectDir = Dir("prune-index-app");
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		WriteCachedPackage(cache, "Foo", "1.0.0");
		WriteCachedPackage(cache, "Foo", "2.0.0");
		File.WriteAllText(Path.Combine(_root, "cache", "pkg", "foo", "index.json"), JsonSerializer.Serialize(new[] { "1.0.0", "2.0.0" }));
		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("2.0.0", ValidHash("foo"), new Dictionary<string, string>())
			}
		}.Write(Path.Combine(projectDir, "cvolo.lock.json"));
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => projectDir, output, error);

		var exitCode = command.Parse("cache prune --unused").Invoke();

		Assert.Equal(0, exitCode);
		var versions = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(_root, "cache", "pkg", "foo", "index.json")));
		Assert.Equal(["2.0.0"], versions);
	}
	[Fact]
	public void PkgList_InvalidLock_ReportsLockOutOfSync()
	{
		var manifest = CreateProject("list-app", Dir("feed"), [new PackageReference("Foo", "1.0.0")]);
		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("1.0.0", "blake3:not-hex", new Dictionary<string, string>())
			}
		}.Write(LockFile.GetPath(manifest));
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => manifest.ProjectDirectory, output, error);

		var exitCode = command.Parse("list").Invoke();

		Assert.Equal(1, exitCode);
		Assert.Contains(PackageDiagnosticIds.LockOutOfSync, error.ToString());
	}
	[Fact]
	public void PkgInstallFromLock_CachedHashMismatch_ReportsCachedContentMismatch()
	{
		var feedDir = Dir("install-cache-feed");
		var archive = CreateArchive(feedDir, "Foo", "1.0.0");
		var manifest = CreateProject("install-cache-app", feedDir, [new PackageReference("Foo", "1.0.0")]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		new PackageInstaller(cache).InstallFromFile(archive);
		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("1.0.0", ValidHash("different"), new Dictionary<string, string>())
			}
		}.Write(LockFile.GetPath(manifest));
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => manifest.ProjectDirectory, output, error);

		var exitCode = command.Parse("install").Invoke();

		Assert.Equal(1, exitCode);
		Assert.Contains(PackageDiagnosticIds.CachedContentMismatch, error.ToString());
	}
	[Fact]
	public void PkgUpdate_RepublishedSameVersion_RejectsStaleCachedContent()
	{
		var feedDir = Dir("republish-feed");
		var original = CreateArchive(feedDir, "Foo", "1.0.0");
		var manifest = CreateProject("republish-app", feedDir, [new PackageReference("Foo", "1.0.0")]);
		var cache = new PackageCache(Path.Combine(_root, "republish-cache"));
		new PackageInstaller(cache).InstallFromFile(original);

		// Re-publish the same identity/version. A new signing key changes the archive bytes/hash.
		CreateArchive(feedDir, "Foo", "1.0.0");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => manifest.ProjectDirectory, output, error);

		var exitCode = command.Parse("update").Invoke();

		Assert.Equal(1, exitCode);
		Assert.Contains(PackageDiagnosticIds.CachedContentMismatch, error.ToString());
	}

	[Fact]
	public void PkgCacheList_MalformedMetadata_IsSkippedWithPathWarning()
	{
		var cache = new PackageCache(Path.Combine(_root, "malformed-list-cache"));
		WriteCachedPackage(cache, "Good", "1.0.0");
		var badDirectory = cache.GetPackageDirectory("Bad", "1.0.0");
		Directory.CreateDirectory(badDirectory);
		var badPath = Path.Combine(badDirectory, ".metadata.json");
		File.WriteAllText(badPath, "{ definitely-not-json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => _root, output, error);

		var exitCode = command.Parse("cache list").Invoke();

		Assert.Equal(0, exitCode);
		Assert.Contains("Good 1.0.0", output.ToString());
		Assert.DoesNotContain("Bad 1.0.0", output.ToString());
		Assert.Contains(badPath, error.ToString());
		Assert.Contains("warning: skipping malformed cache metadata", error.ToString());
	}

	[Fact]
	public void PkgCachePrune_MalformedMetadata_IsSkippedInsteadOfCrashing()
	{
		var cache = new PackageCache(Path.Combine(_root, "malformed-prune-cache"));
		WriteCachedPackage(cache, "Good", "1.0.0");
		var goodDirectory = cache.GetPackageDirectory("Good", "1.0.0");
		var badDirectory = cache.GetPackageDirectory("Bad", "1.0.0");
		Directory.CreateDirectory(badDirectory);
		var badPath = Path.Combine(badDirectory, ".metadata.json");
		File.WriteAllText(badPath, "{ definitely-not-json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var command = new PkgCommand(cache, new PackageInstaller(cache), () => _root, output, error);

		var exitCode = command.Parse("cache prune").Invoke();

		Assert.Equal(0, exitCode);
		Assert.False(Directory.Exists(goodDirectory));
		Assert.True(Directory.Exists(badDirectory));
		Assert.Contains(badPath, error.ToString());
		Assert.Contains("Removed 1 cached package", output.ToString());
	}

	[Fact]
	public void PackageLockValidator_RejectsUnsatisfiedTransitiveDependency()
	{
		var manifest = CreateProject("lock-app", Dir("feed"), [new PackageReference("Foo", "1.0.0")]);
		var lockFile = new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("1.0.0", ValidHash("foo"), new Dictionary<string, string> { ["Bar"] = "^2.0.0" }),
				["Bar"] = new("1.5.0", ValidHash("bar"), new Dictionary<string, string>())
			}
		};

		var valid = PackageLockValidator.Validate(manifest, lockFile, out var message);

		Assert.False(valid);
		Assert.Contains("transitive package 'Bar'", message);
	}

	[Fact]
	public void PackageLockValidator_RejectsMissingTransitiveDependency()
	{
		var manifest = CreateProject("missing-lock-app", Dir("feed"), [new PackageReference("Foo", "1.0.0")]);
		var lockFile = new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("1.0.0", ValidHash("foo"), new Dictionary<string, string> { ["Bar"] = "^1.0.0" })
			}
		};

		var valid = PackageLockValidator.Validate(manifest, lockFile, out var message);

		Assert.False(valid);
		Assert.Contains("missing transitive package 'Bar'", message);
	}

	[Fact]
	public void PackageLockValidator_RejectsInvalidContentHash()
	{
		var manifest = CreateProject("hash-lock-app", Dir("feed"), [new PackageReference("Foo", "1.0.0")]);
		var lockFile = new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("1.0.0", "sha256:foo", new Dictionary<string, string>())
			}
		};

		var valid = PackageLockValidator.Validate(manifest, lockFile, out var message);

		Assert.False(valid);
		Assert.Contains("invalid content hash", message);
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
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		writer.Write(path, key);
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
		File.WriteAllText(Path.Combine(directory, ".metadata.json"), JsonSerializer.Serialize(new InstalledPackageMetadata(id, version, id + ".cvlib", ValidHash(id), DateTimeOffset.UtcNow)));
	}

	private static string ValidHash(string value)
	{
		return "blake3:" + Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(value)).PadRight(64, '0')[..64];
	}

	public void Dispose() => Directory.Delete(_root, true);
}