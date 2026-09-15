using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class ProjectManifestTests : IDisposable
{
	private readonly string _tempDir;

	public ProjectManifestTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "cvolopack_manifest_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { }
	}

	private string Dir(string name)
	{
		var path = Path.Combine(_tempDir, name);
		Directory.CreateDirectory(path);
		return path;
	}

	private static void WriteProject(string dir, string content) => File.WriteAllText(Path.Combine(dir, "Fixture.cvlproj"), content);

	[Fact]
	public void Load_WalksUpFromSubdirectory_ToNearestCvlproj()
	{
		var projectDir = Dir("proj");
		WriteProject(projectDir, """
			<Project Sdk="Cvolo.Sdk">
				<PropertyGroup>
					<PackageId>Acme.Walking</PackageId>
					<Version>2.0.0</Version>
				</PropertyGroup>
			</Project>
			""");
		var sub = Dir(Path.Combine("proj", "Deep", "Nested"));

		var manifest = ProjectManifest.Load(sub);

		Assert.Equal("Acme.Walking", manifest.PackageId);
		Assert.Equal("2.0.0", manifest.Version);
		Assert.Equal(projectDir, manifest.ProjectDirectory);
		Assert.False(manifest.IsLibrary);
	}

	[Fact]
	public void Load_Defaults_AssemblyNameToProjectFileName()
	{
		var projectDir = Dir("defaults");
		WriteProject(projectDir, """
			<Project Sdk="Cvolo.Sdk">
				<PropertyGroup>
					<PackageId>Acme.Defaults</PackageId>
					<Version>1.0.0</Version>
				</PropertyGroup>
			</Project>
			""");

		var manifest = ProjectManifest.Load(projectDir);

		Assert.Equal("Fixture", manifest.OutputName);
		Assert.False(manifest.StrictOption);
		Assert.Empty(manifest.TargetFrameworks);
		Assert.Empty(manifest.Dependencies);
	}

	[Fact]
	public void Load_LibraryOutputType_SetsIsLibrary()
	{
		var projectDir = Dir("lib");
		WriteProject(projectDir, """
			<Project Sdk="Cvolo.Sdk">
				<PropertyGroup>
					<OutputType>Library</OutputType>
					<PackageId>Acme.Lib</PackageId>
					<Version>1.0.0</Version>
				</PropertyGroup>
			</Project>
			""");

		var manifest = ProjectManifest.Load(projectDir);

		Assert.True(manifest.IsLibrary);
	}

	[Fact]
	public void Load_StrictOptionAndTargetFrameworks_AreParsed()
	{
		var projectDir = Dir("strict");
		WriteProject(projectDir, """
			<Project Sdk="Cvolo.Sdk">
				<PropertyGroup>
					<PackageId>Acme.Strict</PackageId>
					<Version>1.0.0</Version>
					<StrictOption>true</StrictOption>
					<TargetFrameworks>win-x64;linux-arm64; other-xyz</TargetFrameworks>
				</PropertyGroup>
			</Project>
			""");

		var manifest = ProjectManifest.Load(projectDir);

		Assert.True(manifest.StrictOption);
		Assert.Equal(new[] { "win-x64", "linux-arm64", "other-xyz" }, manifest.TargetFrameworks);
	}

	[Fact]
	public void Load_Dependencies_AreParsedFromPackageReferenceItems()
	{
		var projectDir = Dir("deps");
		WriteProject(projectDir, """
			<Project Sdk="Cvolo.Sdk">
				<PropertyGroup>
					<PackageId>Acme.Deps</PackageId>
					<Version>1.0.0</Version>
				</PropertyGroup>
				<ItemGroup>
					<PackageReference Include="Acme.Foo" Version="1.2.0" />
					<PackageReference Include="Acme.Bar" Version="^2.0.0" />
				</ItemGroup>
			</Project>
			""");

		var manifest = ProjectManifest.Load(projectDir);

		Assert.Collection(manifest.Dependencies,
			d =>
			{
				Assert.Equal("Acme.Foo", d.Id);
				Assert.Equal("1.2.0", d.Version);
			},
			d =>
			{
				Assert.Equal("Acme.Bar", d.Id);
				Assert.Equal("^2.0.0", d.Version);
			});
	}

	[Fact]
	public void Load_MissingPackageId_ThrowsCVLP3000()
	{
		var projectDir = Dir("nopkgid");
		WriteProject(projectDir, "<Project Sdk=\"Cvolo.Sdk\"><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>");

		var ex = Assert.Throws<PackageException>(() => ProjectManifest.Load(projectDir));

		Assert.Equal(PackageDiagnosticIds.MissingPackageId, ex.Code);
	}

	[Fact]
	public void Load_MissingVersion_ThrowsCVLP3001()
	{
		var projectDir = Dir("nover");
		WriteProject(projectDir, "<Project Sdk=\"Cvolo.Sdk\"><PropertyGroup><PackageId>Acme.NoVer</PackageId></PropertyGroup></Project>");

		var ex = Assert.Throws<PackageException>(() => ProjectManifest.Load(projectDir));

		Assert.Equal(PackageDiagnosticIds.MissingVersion, ex.Code);
	}

	[Fact]
	public void Load_InvalidVersion_ThrowsCVLP3002()
	{
		var projectDir = Dir("badver");
		WriteProject(projectDir, "<Project Sdk=\"Cvolo.Sdk\"><PropertyGroup><PackageId>Acme.BadVer</PackageId><Version>not.a.version</Version></PropertyGroup></Project>");

		var ex = Assert.Throws<PackageException>(() => ProjectManifest.Load(projectDir));

		Assert.Equal(PackageDiagnosticIds.InvalidSemVer, ex.Code);
	}

	[Fact]
	public void Load_NoCvlproj_ThrowsFileNotFound()
	{
		var empty = Dir("nothing");

		Assert.Throws<FileNotFoundException>(() => ProjectManifest.Load(empty));
	}
}
