using System.Text;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Packages;
using Cvolo.Core.Diagnostics;
using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

/// <summary>
/// End-to-end tests for <see cref="PackPipeline.Execute"/>, which compiles a fixture
/// project in-process and assembles a .cvlib archive (Sectors 1/2/3/5).
/// These tests require clang (for object/bitcode emission) and are skipped when absent.
/// </summary>
public sealed class PackPipelineTests : IDisposable
{
	private readonly string _tempDir;

	public PackPipelineTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "cvolopack_tests_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { }
	}

	private string Fixture(string name) => Path.Combine(_tempDir, name);

	private const string ProjectTemplate = """
		<Project Sdk="Cvolo.Sdk">
			<PropertyGroup>
				<OutputType>{0}</OutputType>
				<AssemblyName>FixtureApp</AssemblyName>
				<PackageId>Acme.Fixture</PackageId>
				<Version>1.2.3</Version>
			</PropertyGroup>
		</Project>
		""";

	private static string CreateProject(string dir, string outputType = "Exe")
	{
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "Fixture.cvlproj"), string.Format(ProjectTemplate, outputType));
		return dir;
	}

	private static void RequireClang()
	{
		if (FindClang() is null)
			Assert.Skip("clang not available; cvolo pack requires clang for object/bitcode emission.");
	}

	private static string? FindClang()
	{
		var names = OperatingSystem.IsWindows() ? new[] { "clang.exe", "clang" } : new[] { "clang" };
		foreach (var baseDir in new[] { AppContext.BaseDirectory })
		{
			foreach (var name in names)
			{
				var candidate = Path.Combine(baseDir, name);
				if (File.Exists(candidate))
					return candidate;
			}
		}

		foreach (var directory in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
		{
			if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
				continue;

			foreach (var name in names)
			{
				var candidate = Path.Combine(directory, name);
				if (File.Exists(candidate))
					return candidate;
			}
		}

		return null;
	}

	private static PackOptions DefaultOptions(string outputPath, string[] targets, bool stripSource = false, string? signingKeyPath = null, bool noSign = false) => new()
	{
		Targets = targets,
		OutputPath = outputPath,
		Profile = LinkageProfile.StripBinaries,
		StripSource = stripSource,
		SigningKeyPath = signingKeyPath,
		NoSign = noSign,
		Verbose = false
	};

	[Fact]
	public void Pack_ExecutableProject_WritesValidArchiveToDefaultBinPath()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("exe"));
		File.WriteAllText(Path.Combine(projectDir, "Main.cvl"), "int Main() { return 0; }");

		var hostTriple = TargetTriple.HostTriple();
		var result = PackPipeline.Execute(projectDir, DefaultOptions(string.Empty, [hostTriple]));

		Assert.Equal("Acme.Fixture", result.PackageId);
		Assert.Equal("1.2.3", result.Version);
		var expectedPath = Path.Combine(projectDir, "bin", "Acme.Fixture.cvlib");
		Assert.Equal(expectedPath, result.OutputPath);
		Assert.True(File.Exists(expectedPath), "The .cvlib archive must exist at the default bin path.");
		Assert.Single(result.Targets);
		Assert.Equal(TargetTriple.Resolve(hostTriple), result.Targets[0]);
		Assert.True(result.FileSize > 0);
		Assert.Equal(64, result.MerkleRootHex.Length);
		// v0.2.6 install/build verification requires ordinary pack output to be signed.
		using var verified = CvlArchiveReader.Read(result.OutputPath);
		Assert.Single(verified.Manifest.Slices);
	}

	[Fact]
	public void Pack_SingleTarget_ArchivePayloadsMatchManifestRanges()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("single"));
		const string source = "int Main() { return 0; }";
		File.WriteAllText(Path.Combine(projectDir, "Main.cvl"), source);

		var hostTriple = TargetTriple.HostTriple();
		var outputPath = Fixture(Path.Combine("out", "single.cvlib"));
		var result = PackPipeline.Execute(projectDir, DefaultOptions(outputPath, [hostTriple]));

		using var archive = CvlArchiveReader.Read(result.OutputPath, verifySignature: false);
		var triple = TargetTriple.Resolve(hostTriple);

		Assert.Equal(5UL, archive.Header.SectorCount);
		Assert.Single(archive.Manifest.Slices);
		Assert.Equal(triple, archive.Manifest.Slices[0].Triple);
		Assert.Equal(0UL, archive.Manifest.Slices[0].Sector2.Offset);
		Assert.Equal(0UL, archive.Manifest.Slices[0].Sector3.Offset);

		var objects = archive.GetSectorPayload(2);
		var bitcode = archive.GetSectorPayload(3);
		Assert.Equal(objects.Length, (int)archive.Manifest.Slices[0].Sector2.Length);
		Assert.Equal(bitcode.Length, (int)archive.Manifest.Slices[0].Sector3.Length);
		Assert.True(objects.Length > 0);
		Assert.True(bitcode.Length > 0);

		Assert.Equal(source, archive.ReadSourceBuffer());
	}

	[Fact]
	public void Pack_MultiTarget_ProducesOneSlicePerTriple()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("multi"));
		File.WriteAllText(Path.Combine(projectDir, "Main.cvl"), "int Main() { return 0; }");

		var targets = new[] { TargetTriple.Resolve("win-x64"), TargetTriple.Resolve("linux-arm64") };
		var result = PackPipeline.Execute(projectDir, DefaultOptions(Fixture(Path.Combine("out", "multi.cvlib")), [targets[0], targets[1]]));

		Assert.Equal(2, result.Targets.Count);

		using var archive = CvlArchiveReader.Read(result.OutputPath, verifySignature: false);
		Assert.Equal(2, archive.Manifest.Slices.Count);
		Assert.Equal(targets[0], archive.Manifest.Slices[0].Triple);
		Assert.Equal(targets[1], archive.Manifest.Slices[1].Triple);

		// Sector 2 concatenates the objects: slice 2 starts exactly where slice 1 ends.
		Assert.Equal(0UL, archive.Manifest.Slices[0].Sector2.Offset);
		Assert.Equal(archive.Manifest.Slices[0].Sector2.Length, archive.Manifest.Slices[1].Sector2.Offset);

		// Sector 3 carries target-specific bitcode ranges in the same deterministic order.
		Assert.Equal(0UL, archive.Manifest.Slices[0].Sector3.Offset);
		Assert.Equal(archive.Manifest.Slices[0].Sector3.Length, archive.Manifest.Slices[1].Sector3.Offset);
		var bitcode = archive.GetSectorPayload(3);
		Assert.Equal(
			archive.Manifest.Slices[0].Sector3.Length + archive.Manifest.Slices[1].Sector3.Length,
			(ulong)bitcode.Length);
		Assert.True(archive.Manifest.Slices.All(slice => slice.Sector3.Length > 0));
	}

	[Fact]
	public void Pack_StripSource_OmitsSector5()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("strip"));
		File.WriteAllText(Path.Combine(projectDir, "Main.cvl"), "int Main() { return 0; }");

		var options = DefaultOptions(Fixture(Path.Combine("out", "strip.cvlib")), [TargetTriple.HostTriple()], stripSource: true);

		var result = PackPipeline.Execute(projectDir, options);

		using var archive = CvlArchiveReader.Read(result.OutputPath, verifySignature: false);
		Assert.Equal(string.Empty, archive.ReadSourceBuffer());
	}

	[Fact]
	public void Pack_LibraryProject_DoesNotRequireMain()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("lib"), "Library");
		File.WriteAllText(Path.Combine(projectDir, "Api.cvl"), "namespace TestLibrary;\n\npublic int Add(int a, int b) { return a + b; }");

		var manifest = ProjectManifest.Load(projectDir);
		Assert.True(manifest.IsLibrary);

		var result = PackPipeline.Execute(projectDir, DefaultOptions(Fixture(Path.Combine("out", "lib.cvlib")), [TargetTriple.HostTriple()]));

		using var archive = CvlArchiveReader.Read(result.OutputPath, verifySignature: false);
		Assert.Single(archive.Manifest.Slices);
	}

	[Fact]
	public void Pack_LibraryProject_StoresPublicCvoloApiMetadata()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("api-metadata"), "Library");
		File.WriteAllText(Path.Combine(projectDir, "Api.cvl"), """
namespace TestLibrary;

public struct Pair {
    public int Left;
    public int Right;
}

public enum Mode : byte { Slow = 0, Fast = 1 }
public global int Bias = 5;
public int Add(int a, int b) { return a + b; }
int Hidden(int value) { return value; }
""");

		var result = PackPipeline.Execute(projectDir, DefaultOptions(
			Fixture(Path.Combine("out", "api-metadata.cvlib")),
			[TargetTriple.HostTriple()],
			stripSource: true));

		using var archive = CvlArchiveReader.Read(result.OutputPath, verifySignature: false);
		Assert.Equal(string.Empty, archive.ReadSourceBuffer());

		var api = PackageApiMetadata.Read(archive);
		var unit = Assert.Single(api.Units);
		Assert.Equal("TestLibrary", unit.Namespace);
		var function = Assert.Single(unit.Functions);
		Assert.Equal("Add", function.Name);
		Assert.Equal("int", function.ReturnType);
		Assert.Collection(
			function.Parameters,
			parameter => { Assert.Equal("int", parameter.Type); Assert.Equal("a", parameter.Name); },
			parameter => { Assert.Equal("int", parameter.Type); Assert.Equal("b", parameter.Name); });

		var type = Assert.Single(unit.Structs);
		Assert.Equal("Pair", type.Name);
		Assert.Collection(type.Fields,
			field => { Assert.Equal("int", field.Type); Assert.Equal("Left", field.Name); Assert.Equal(Visibility.Public, field.Visibility); },
			field => { Assert.Equal("int", field.Type); Assert.Equal("Right", field.Name); Assert.Equal(Visibility.Public, field.Visibility); });

		var enumType = Assert.Single(unit.Enums);
		Assert.Equal("Mode", enumType.Name);
		Assert.Equal("byte", enumType.StorageType);
		Assert.Equal(["Slow", "Fast"], enumType.Variants.Select(variant => variant.Name).ToArray());

		var global = Assert.Single(unit.Globals);
		Assert.Equal("Bias", global.Name);
		Assert.Equal("int", global.Type);
		Assert.False(global.IsMutable);
	}

	[Fact]
	public void Pack_Signed_ArchiveVerifiesWithGivenKey()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("signed"));
		File.WriteAllText(Path.Combine(projectDir, "Main.cvl"), "int Main() { return 0; }");

		var seedPath = Fixture("signing.seed");
		var seed = new byte[32];
		for (var i = 0; i < seed.Length; i++)
			seed[i] = (byte)i;
		File.WriteAllBytes(seedPath, seed);

		var options = DefaultOptions(Fixture(Path.Combine("out", "signed.cvlib")), [TargetTriple.HostTriple()], signingKeyPath: seedPath);

		var result = PackPipeline.Execute(projectDir, options);

		// A fresh read with signature verification enabled must succeed.
		using var archive = CvlArchiveReader.Read(result.OutputPath, verifySignature: true);
		Assert.Equal(64, result.MerkleRootHex.Length);
	}

	[Fact]
	public void Pack_InvalidSigningKeyLength_Throws()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("badkey"));
		File.WriteAllText(Path.Combine(projectDir, "Main.cvl"), "int Main() { return 0; }");

		var seedPath = Fixture("short.seed");
		File.WriteAllBytes(seedPath, new byte[8]);

		var options = DefaultOptions(Fixture(Path.Combine("out", "badkey.cvlib")), [TargetTriple.HostTriple()], signingKeyPath: seedPath);

		var ex = Assert.Throws<InvalidOperationException>(() => PackPipeline.Execute(projectDir, options));
		Assert.Contains("32", ex.Message);
	}

	[Fact]
	public void Pack_AnalysisError_ThrowsPackageExceptionWithAnalysisCode()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("analyze"));
		File.WriteAllText(Path.Combine(projectDir, "Main.cvl"), "int Main() { unresolved_thing(); return 0; }");

		var ex = Assert.Throws<PackageException>(() => PackPipeline.Execute(projectDir, DefaultOptions(Fixture(Path.Combine("out", "analyze.cvlib")), [])));
		Assert.Equal("ANALYSIS", ex.Code);
	}

	[Fact]
	public void Pack_ExecutableWithoutMain_ThrowsEntryPoint()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("nomain"));
		File.WriteAllText(Path.Combine(projectDir, "Api.cvl"), "public struct Config { public int Width; }");

		var ex = Assert.Throws<PackageException>(() => PackPipeline.Execute(projectDir, DefaultOptions(Fixture(Path.Combine("out", "nomain.cvlib")), [])));
		Assert.Equal("ENTRYPOINT", ex.Code);
	}

	[Fact]
	public void Pack_ProjectWithoutSources_Throws()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("emptyproj"));

		var ex = Assert.Throws<InvalidOperationException>(() => PackPipeline.Execute(projectDir, DefaultOptions(Fixture(Path.Combine("out", "empty.cvlib")), [])));
		Assert.Contains("No .cvl source files", ex.Message);
	}

	[Fact]
	public void Pack_NoProjectFile_ThrowsFileNotFound()
	{
		var emptyDir = Fixture("noproj");
		Directory.CreateDirectory(emptyDir);

		Assert.Throws<FileNotFoundException>(() => PackPipeline.Execute(emptyDir, DefaultOptions(Fixture(Path.Combine("out", "noproj.cvlib")), [])));
	}

	[Fact]
	public void Pack_SourceBuffer_ConcatenatesAllProjectFilesInRelPathOrder()
	{
		RequireClang();
		var projectDir = CreateProject(Fixture("multifile"));
		Directory.CreateDirectory(Path.Combine(projectDir, "Sub"));
		File.WriteAllText(Path.Combine(projectDir, "Sub", "Util.cvl"), "public int Twice(int x) { return x * 2; }", new UTF8Encoding(false));
		File.WriteAllText(Path.Combine(projectDir, "A.cvl"), "int Main() { return 0; }", new UTF8Encoding(false));

		var result = PackPipeline.Execute(projectDir, DefaultOptions(Fixture(Path.Combine("out", "multifile.cvlib")), [TargetTriple.HostTriple()]));

		using var archive = CvlArchiveReader.Read(result.OutputPath, verifySignature: false);
		var sourceBuffer = archive.ReadSourceBuffer();
		// Rel-path order: "A.cvl" sorts before "Sub\Util.cvl".
		var expected = "int Main() { return 0; }\npublic int Twice(int x) { return x * 2; }";
		Assert.Equal(expected, sourceBuffer);
	}
}