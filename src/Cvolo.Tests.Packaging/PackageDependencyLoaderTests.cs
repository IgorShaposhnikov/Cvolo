using System.Buffers.Binary;
using System.Text.Json;
using Cvolo.Core.Packages;
using Cvolo.Packaging;
using NSec.Cryptography;

namespace Cvolo.Tests.Packaging;

public sealed class PackageDependencyLoaderTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-dep-loader-" + Guid.NewGuid().ToString("N"));

	public PackageDependencyLoaderTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void Load_ExtractsLockedHostObjectIntoDeterministicProjectDirectory()
	{
		var project = CreateProject("app", [new PackageReference("Foo", "1.0.0")]);
		var source = CreateArchive("Foo", "1.0.0", [0x10, 0x20, 0x30, 0x40]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		new PackageInstaller(cache).InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", LocalFeed.ComputeHash(source));

		var artifacts = new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, lockFile);

		var artifact = Assert.Single(artifacts);
		Assert.Equal("Foo", artifact.PackageId);
		Assert.Equal("1.0.0", artifact.Version);
		Assert.NotNull(artifact.NativeObjectPath);
		Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, File.ReadAllBytes(artifact.NativeObjectPath!));
		Assert.Equal(new byte[] { 0x42 }, File.ReadAllBytes(artifact.BitcodePath!));
		Assert.StartsWith(Path.Combine(project.ProjectDirectory, ".cvolo", "build"), artifact.BitcodePath!, StringComparison.OrdinalIgnoreCase);
		Assert.EndsWith(Path.Combine("foo", "1.0.0", "Foo.bc"), artifact.BitcodePath!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Load_Twice_ProducesByteIdenticalObjects()
	{
		var project = CreateProject("determinism-app", [new PackageReference("Foo", "1.0.0")]);
		var source = CreateArchive("Foo", "1.0.0", [0xDE, 0xAD, 0xBE, 0xEF]);
		var cache = new PackageCache(Path.Combine(_root, "cache-determinism"));
		var installer = new PackageInstaller(cache);
		installer.InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", LocalFeed.ComputeHash(source));
		var loader = new PackageDependencyLoader(cache, installer);

		var first = Assert.Single(loader.Load(project, lockFile));
		var firstObjectBytes = File.ReadAllBytes(first.NativeObjectPath!);
		var firstBitcodeBytes = File.ReadAllBytes(first.BitcodePath!);
		var firstObjectPath = first.NativeObjectPath;
		var firstBitcodePath = first.BitcodePath;

		var second = Assert.Single(loader.Load(project, lockFile));
		var secondObjectBytes = File.ReadAllBytes(second.NativeObjectPath!);
		var secondBitcodeBytes = File.ReadAllBytes(second.BitcodePath!);

		Assert.Equal(firstObjectPath, second.NativeObjectPath);
		Assert.Equal(firstBitcodePath, second.BitcodePath);
		Assert.Equal(firstObjectBytes, secondObjectBytes);
		Assert.Equal(firstBitcodeBytes, secondBitcodeBytes);
	}

	[Fact]
	public async Task Load_ConcurrentBuilds_ShareAtomicExtractionWithoutDeletingEachOthersArtifacts()
	{
		var project = CreateProject("parallel-app", [new PackageReference("Foo", "1.0.0")]);
		var source = CreateArchive("Foo", "1.0.0", [0xCA, 0xFE, 0xBA, 0xBE]);
		var cache = new PackageCache(Path.Combine(_root, "cache-parallel"));
		var installer = new PackageInstaller(cache);
		installer.InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", LocalFeed.ComputeHash(source));
		var loader = new PackageDependencyLoader(cache, installer);

		var loads = Enumerable.Range(0, 8)
			.Select(_ => Task.Run(() => Assert.Single(loader.Load(project, lockFile))))
			.ToArray();
		var artifacts = await Task.WhenAll(loads);

		var first = artifacts[0];
		Assert.All(artifacts, artifact =>
		{
			Assert.Equal(first.NativeObjectPath, artifact.NativeObjectPath);
			Assert.Equal(first.BitcodePath, artifact.BitcodePath);
			Assert.Equal(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, File.ReadAllBytes(artifact.NativeObjectPath!));
			Assert.Equal(new byte[] { 0x42 }, File.ReadAllBytes(artifact.BitcodePath!));
		});

		var packageDirectory = Path.GetDirectoryName(first.BitcodePath!)!;
		Assert.Empty(Directory.EnumerateFiles(packageDirectory, "*.tmp_*", SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public void Load_UsesAllLockedPackagesIncludingTransitivesInDeterministicOrder()
	{
		var project = CreateProject("transitive-app", [new PackageReference("Zulu", "1.0.0")]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		var zulu = CreateArchive("Zulu", "1.0.0", [0x5A]);
		var alpha = CreateArchive("Alpha", "2.0.0", [0x41]);
		new PackageInstaller(cache).InstallFromFile(zulu);
		new PackageInstaller(cache).InstallFromFile(alpha);
		var lockFile = new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Zulu"] = new("1.0.0", LocalFeed.ComputeHash(zulu), new Dictionary<string, string> { ["Alpha"] = "2.0.0" }),
				["Alpha"] = new("2.0.0", LocalFeed.ComputeHash(alpha), new Dictionary<string, string>())
			}
		};
		lockFile.Write(LockFile.GetPath(project));

		var artifacts = new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, LockFile.Read(LockFile.GetPath(project)));

		Assert.Equal(["Alpha", "Zulu"], artifacts.Select(a => a.PackageId).ToArray());
	}

	[Fact]
	public void Load_BitcodeOnlyHostSlice_IsValidForPhase4Build()
	{
		var project = CreateProject("bitcode-app", [new PackageReference("Foo", "1.0.0")]);
		var source = CreateArchive("Foo", "1.0.0", []);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		new PackageInstaller(cache).InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", LocalFeed.ComputeHash(source));

		var artifact = Assert.Single(new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, lockFile));

		Assert.Null(artifact.NativeObjectPath);
		Assert.Equal(new byte[] { 0x42 }, File.ReadAllBytes(artifact.BitcodePath!));
	}

	[Fact]
	public void Load_NativeObjectOnlyHostSlice_ReportsBitcodeUnavailable()
	{
		var project = CreateProject("object-only-app", [new PackageReference("Foo", "1.0.0")]);
		var source = CreateArchive("Foo", "1.0.0", [0x10], bitcodeBytes: []);
		var cache = new PackageCache(Path.Combine(_root, "cache-object-only"));
		new PackageInstaller(cache).InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", LocalFeed.ComputeHash(source));

		var ex = Assert.Throws<PackageException>(() => new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, lockFile));

		Assert.Equal(PackageDiagnosticIds.BitcodeUnavailable, ex.Code);
	}

	[Fact]
	public void Load_ObjectOnlyHostSlice_WithSector5_UsesSourceFallback()
	{
		var project = CreateProject("source-fallback-app", [new PackageReference("Foo", "1.0.0")]);
		var source = CreateArchive(
			"Foo", "1.0.0", [0x10], bitcodeBytes: [],
			sourceBuffer: "namespace Foo; public int Add(int a, int b) { return a + b; }");
		var cache = new PackageCache(Path.Combine(_root, "cache-source-fallback"));
		new PackageInstaller(cache).InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", LocalFeed.ComputeHash(source));

		var artifact = Assert.Single(new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, lockFile));

		Assert.True(artifact.UsesSourceFallback);
		Assert.Null(artifact.BitcodePath);
		Assert.Null(artifact.NativeObjectPath);
		Assert.Empty(artifact.TemplateUnits);
		Assert.Single(artifact.SourceFallbackUnits);
	}

	[Fact]
	public void Install_ForeignSlice_WithSector5_RewritesCacheForHostSourceFallback()
	{
		var project = CreateProject("foreign-source-fallback-app", [new PackageReference("Foo", "1.0.0")]);
		var foreignTriple = TargetTriple.HostTriple() + "-foreign";
		var source = CreateArchive(
			"Foo", "1.0.0", [0x10], bitcodeBytes: [0x42],
			sourceBuffer: "namespace Foo; public int Add(int a, int b) { return a + b; }",
			triple: foreignTriple);
		var cache = new PackageCache(Path.Combine(_root, "cache-foreign-source-fallback"));
		new PackageInstaller(cache).InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", LocalFeed.ComputeHash(source));

		var artifact = Assert.Single(new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, lockFile));

		Assert.True(artifact.UsesSourceFallback);
		Assert.Null(artifact.BitcodePath);
		Assert.Single(artifact.SourceFallbackUnits);
	}

	[Fact]
	public void Load_MissingCachedPackage_ReportsLockOutOfSync()
	{
		var project = CreateProject("missing-app", [new PackageReference("Foo", "1.0.0")]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		var lockFile = WriteLock(project, "Foo", "1.0.0", ValidHash("foo"));

		var ex = Assert.Throws<PackageException>(() => new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, lockFile));

		Assert.Equal(PackageDiagnosticIds.LockOutOfSync, ex.Code);
		Assert.Contains("cvolo pkg install", ex.Message);
	}

	[Fact]
	public void Load_CacheMetadataHashMismatch_ReportsCVLP3032()
	{
		var project = CreateProject("hash-app", [new PackageReference("Foo", "1.0.0")]);
		var source = CreateArchive("Foo", "1.0.0", [0x01, 0x02]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		new PackageInstaller(cache).InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", ValidHash("different"));

		var ex = Assert.Throws<PackageException>(() => new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, lockFile));

		Assert.Equal(PackageDiagnosticIds.BuildCacheContentMismatch, ex.Code);
	}

	[Fact]
	public void Load_TamperedCachedArchive_WithoutSourceFallback_ReportsCVLF1904()
	{
		var project = CreateProject("tamper-app", [new PackageReference("Foo", "1.0.0")]);
		var source = CreateArchive("Foo", "1.0.0", [0x01, 0x02, 0x03]);
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		var installed = new PackageInstaller(cache).InstallFromFile(source);
		var lockFile = WriteLock(project, "Foo", "1.0.0", LocalFeed.ComputeHash(source));
		long sector2Offset;
		using (var archive = CvlArchiveReader.Read(installed.OutputPath, verifySignature: false))
			sector2Offset = checked((long)archive.Sectors[1].Offset);
		using (var stream = File.OpenWrite(installed.OutputPath))
		{
			stream.Position = sector2Offset;
			stream.WriteByte(0xFF);
		}

		var ex = Assert.Throws<PackageException>(() => new PackageDependencyLoader(cache, new PackageInstaller(cache)).Load(project, lockFile));

		Assert.Equal(CvlFormatDiagnosticIds.FatalStripSource, ex.Code);
	}

	private ProjectManifest CreateProject(string name, IReadOnlyList<PackageReference> dependencies)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		var references = string.Join(Environment.NewLine, dependencies.Select(d => $"    <PackageReference Include=\"{d.Id}\" Version=\"{d.Version}\" />"));
		var itemGroup = dependencies.Count == 0 ? string.Empty : $"\n  <ItemGroup>\n{references}\n  </ItemGroup>";
		File.WriteAllText(Path.Combine(directory, "App.cvlproj"), $$"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <PackageId>App</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>{{itemGroup}}
</Project>
""");
		return ProjectManifest.Load(directory);
	}

	private string CreateArchive(
		string id,
		string version,
		byte[] objectBytes,
		byte[]? bitcodeBytes = null,
		string? sourceBuffer = null,
		string? triple = null)
	{
		var path = Path.Combine(_root, $"{id}.{version}.{Guid.NewGuid():N}.cvlib");
		bitcodeBytes ??= [0x42];
		triple ??= TargetTriple.HostTriple();
		var slice = new CvlSliceEntry(triple, new(0, (ulong)objectBytes.Length), new(0, (ulong)bitcodeBytes.Length));
		var json = JsonSerializer.SerializeToUtf8Bytes(new
		{
			Format = "cvlib.slice-manifest.v1",
			PackageId = id,
			Version = version,
			Dependencies = Array.Empty<PackageReference>(),
			Slices = new[] { slice }
		});
		var sector1 = new byte[json.Length + 6];
		BinaryPrimitives.WriteUInt32LittleEndian(sector1, (uint)json.Length);
		json.CopyTo(sector1, 4);
		"{}"u8.CopyTo(sector1.AsSpan(4 + json.Length));
		var writer = new CvlArchiveWriter();
		writer.SetSector(1, sector1);
		writer.SetSector(2, objectBytes);
		writer.SetSector(3, bitcodeBytes);
		if (sourceBuffer is not null)
			writer.SetSourceBuffer(sourceBuffer);
		using var key = Key.Create(SignatureAlgorithm.Ed25519);
		writer.Write(path, key);
		return path;
	}

	private static LockFile WriteLock(ProjectManifest project, string id, string version, string contentHash)
	{
		var lockFile = new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				[id] = new(version, contentHash, new Dictionary<string, string>())
			}
		};
		lockFile.Write(LockFile.GetPath(project));
		return LockFile.Read(LockFile.GetPath(project));
	}

	private static string ValidHash(string value) =>
		"blake3:" + Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(value)).PadRight(64, '0')[..64];

	public void Dispose()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}
}
