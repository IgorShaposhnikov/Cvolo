using System.Xml.Linq;
using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

/// <summary>
/// Application-Native-Library-Resolution INC: <NativeLibraries> parsing/validation and
/// most-specific mapping selection (no compiler invocation; pure in-process units).
/// </summary>
public sealed class NativeLibrariesSelectionTests : IDisposable
{
	private readonly string _tempDir;

	public NativeLibrariesSelectionTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "cvolonativelibs_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_tempDir, recursive: true);
		} catch { }
	}

	private static NativeLibrariesGroup Group(NativeLibraryTarget target, string? arch, string? configuration, params (string For, string Path)[] libraries)
		=> new(target, arch, configuration,
			libraries.Select(library => new NativeLibraryDeclaration(library.For, library.Path, Array.Empty<string>())).ToList());

	private static (IReadOnlyList<NativeLibrariesGroup> Groups, List<(string Code, string Message)> Warnings) ParseXml(string nativeXml)
	{
		var root = XDocument.Parse($"<Project Sdk=\"Cvolo.Sdk\">{nativeXml}</Project>").Root!;
		var warnings = new List<(string Code, string Message)>();
		var groups = NativeLibrariesParser.Parse(root, warnings);
		return (groups, warnings);
	}

	private static void AssertRejects(string expectedCode, string nativeXml)
	{
		var ex = Assert.Throws<PackageException>(() => ParseXml(nativeXml));
		Assert.Equal(expectedCode, ex.Code);
	}

	// Selection: target / arch / configuration

	[Fact]
	public void Select_TargetOS_WindowsLinuxMacos()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.Windows, null, null, ("glfw3", "win/glfw3.lib")),
			Group(NativeLibraryTarget.Linux, null, null, ("glfw3", "linux/libglfw3.so")),
			Group(NativeLibraryTarget.MacOS, null, null, ("glfw3", "mac/libglfw3.dylib"))
		};

		Assert.Equal("win/glfw3.lib", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, "x64", "Debug").Declaration!.Path);
		Assert.Equal("linux/libglfw3.so", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Linux, "x64", "Debug").Declaration!.Path);
		Assert.Equal("mac/libglfw3.dylib", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.MacOS, "arm64", "Release").Declaration!.Path);
	}

	[Fact]
	public void Select_TargetMustMatch_NoHostFallbackToWindows()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.Linux, null, null, ("glfw3", "linux/libglfw3.so"))
		};

		var selection = NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, "x64", "Debug");

		Assert.Equal(NativeLibraryMatchKind.NotFound, selection.Kind);
	}

	[Fact]
	public void Select_MostSpecificOverridesGeneric()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.Windows, null, null, ("glfw3", "any/glfw3.lib")),
			Group(NativeLibraryTarget.Windows, "x64", null, ("glfw3", "x64/glfw3.lib"))
		};

		Assert.Equal("x64/glfw3.lib", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, "x64", "Debug").Declaration!.Path);
		Assert.Equal("any/glfw3.lib", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, "arm64", "Debug").Declaration!.Path);
	}

	[Fact]
	public void Select_MoreSpecificBeatsBothPartiallySpecificMatches()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.Windows, null, null, ("glfw3", "any/glfw3.lib")),
			Group(NativeLibraryTarget.Windows, "x64", null, ("glfw3", "x64/glfw3.lib")),
			Group(NativeLibraryTarget.Windows, null, "Debug", ("glfw3", "debug/glfw3.lib")),
			Group(NativeLibraryTarget.Windows, "x64", "Debug", ("glfw3", "x64-debug/glfw3.lib"))
		};

		var selection = NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, "x64", "Debug");

		Assert.Equal(NativeLibraryMatchKind.Selected, selection.Kind);
		Assert.Equal("x64-debug/glfw3.lib", selection.Declaration!.Path);
	}

	[Fact]
	public void Select_IncomparableMatches_AreAmbiguous()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.Windows, "x64", null, ("glfw3", "x64/glfw3.lib")),
			Group(NativeLibraryTarget.Windows, null, "Debug", ("glfw3", "debug/glfw3.lib"))
		};

		var selection = NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, "x64", "Debug");

		Assert.Equal(NativeLibraryMatchKind.Ambiguous, selection.Kind);
		Assert.NotNull(selection.Group);
		Assert.NotNull(selection.Conflict);
	}

	[Fact]
	public void Select_MacOSWithoutArch_MatchesBothArches()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.MacOS, null, null, ("glfw3", "universal/libglfw3.dylib"))
		};

		Assert.Equal("universal/libglfw3.dylib", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.MacOS, "x64", "Debug").Declaration!.Path);
		Assert.Equal("universal/libglfw3.dylib", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.MacOS, "arm64", "Release").Declaration!.Path);
	}

	[Fact]
	public void Select_ArchSpecificGroup_OnlyMatchesItsArch()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.Windows, "arm64", null, ("glfw3", "arm64/glfw3.lib"))
		};

		Assert.Equal("arm64/glfw3.lib", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, "arm64", null).Declaration!.Path);
		Assert.Equal(NativeLibraryMatchKind.NotFound, NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, "x64", null).Kind);
	}

	// Selection: opaque, case-sensitive logical names

	[Fact]
	public void Select_LogicalNameIsOpaque_DotsAndDashes()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.Windows, null, null,
				("vulkan-1", "libs/vulkan-1.lib"),
				("libfoo.bar", "libs/libfoo.bar.lib"),
				("glfw3", "libs/glfw3.lib"))
		};

		Assert.Equal("libs/vulkan-1.lib", NativeLibrarySelector.Select(groups, "vulkan-1", NativeLibraryTarget.Windows, null, null).Declaration!.Path);
		Assert.Equal("libs/libfoo.bar.lib", NativeLibrarySelector.Select(groups, "libfoo.bar", NativeLibraryTarget.Windows, null, null).Declaration!.Path);
		Assert.Equal(NativeLibraryMatchKind.NotFound, NativeLibrarySelector.Select(groups, "libfoo", NativeLibraryTarget.Windows, null, null).Kind);
		Assert.Equal(NativeLibraryMatchKind.NotFound, NativeLibrarySelector.Select(groups, "vulkan", NativeLibraryTarget.Windows, null, null).Kind);
	}

	[Fact]
	public void Select_LogicalNameCaseSensitive_NotSilentlyMerged()
	{
		var groups = new List<NativeLibrariesGroup>
		{
			Group(NativeLibraryTarget.Windows, null, null, ("glfw3", "libs/glfw3.lib"))
		};

		Assert.Equal("libs/glfw3.lib", NativeLibrarySelector.Select(groups, "glfw3", NativeLibraryTarget.Windows, null, null).Declaration!.Path);
		Assert.Equal(NativeLibraryMatchKind.NotFound, NativeLibrarySelector.Select(groups, "GLFW3", NativeLibraryTarget.Windows, null, null).Kind);
		Assert.Equal(NativeLibraryMatchKind.NotFound, NativeLibrarySelector.Select(groups, "Glfw3", NativeLibraryTarget.Windows, null, null).Kind);
	}

	// Parsing: selectors are case/whitespace-insensitive

	[Fact]
	public void Parse_TargetArchConfiguration_CaseAndWhitespaceInsensitive()
	{
		var (groups, _) = ParseXml("""
			<NativeLibraries Target="  WiNdOwS " Arch=" X64 " Configuration=" dEbUg ">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			</NativeLibraries>
			""");

		var group = Assert.Single(groups);
		Assert.Equal(NativeLibraryTarget.Windows, group.Target);
		Assert.Equal("x64", group.Arch);
		Assert.Equal("Debug", group.Configuration);
		Assert.Equal("glfw3", Assert.Single(group.Libraries).For);
	}

	[Fact]
	public void Parse_UnknownTargetRejected_NoWindowsFallback()
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesUnknownTarget, """
			<NativeLibraries Target="windwos">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			</NativeLibraries>
			""");
	}

	[Theory]
	[InlineData("win")]
	[InlineData("mac")]
	[InlineData("osx")]
	[InlineData("Win32")]
	public void Parse_NonCanonicalTargetAliasRejected(string target)
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesUnknownTarget, $"""
			<NativeLibraries Target="{target}">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			</NativeLibraries>
			""");
	}

	[Fact]
	public void Parse_UnknownArchitectureRejected()
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesUnknownArchitecture, """
			<NativeLibraries Target="linux" Arch="riscv">
			  <NativeLibrary For="glfw3" Path="libs/libglfw3.so" />
			</NativeLibraries>
			""");
	}

	[Fact]
	public void Parse_UnknownConfigurationRejected()
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesUnknownConfiguration, """
			<NativeLibraries Target="windows" Configuration="RelWithDebInfo">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			</NativeLibraries>
			""");
	}

	[Fact]
	public void Parse_MissingTargetRejected()
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesInvalidStructure, """
			<NativeLibraries Arch="x64">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			</NativeLibraries>
			""");
	}

	// Parsing: no Condition / expression language

	[Fact]
	public void Parse_NoConditionExpressionEvaluation()
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesInvalidStructure, """
			<NativeLibraries Target="windows" Condition="'$(Configuration)' == 'Debug'">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			</NativeLibraries>
			""");
	}

	[Fact]
	public void Parse_UnexpectedNestedElementRejected()
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesInvalidStructure, """
			<NativeLibraries Target="windows">
			  <PropertyGroup><DefineConstants>NATIVE</DefineConstants></PropertyGroup>
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			</NativeLibraries>
			""");
	}

	[Fact]
	public void Parse_UnexpectedAttributesRejected()
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesInvalidStructure, """
			<NativeLibraries Target="windows">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" Hint="optional">
			  </NativeLibrary>
			</NativeLibraries>
			""");

		AssertRejects(PackageDiagnosticIds.NativeLibrariesInvalidStructure, """
			<NativeLibraries Target="windows">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib">
			    <RuntimeFile Path="libs/glfw3.dll" CopyLocal="false" />
			  </NativeLibrary>
			</NativeLibraries>
			""");
	}

	[Theory]
	[InlineData("%USERPROFILE%\\libs\\glfw3.lib")]
	[InlineData("$(Configuration)\\glfw3.lib")]
	[InlineData("${HOME}/libglfw3.so")]
	public void Parse_EnvironmentOrMsbuildPathExpressionRejected(string path)
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesInvalidPath, $"""
			<NativeLibraries Target="windows">
			  <NativeLibrary For="glfw3" Path="{path}" />
			</NativeLibraries>
			""");
	}

	[Theory]
	[InlineData("<NativeLibraries Target=\"windows\"><NativeLibrary For=\"glfw3\" Path=\"  \" /></NativeLibraries>")]
	[InlineData("<NativeLibraries Target=\"windows\"><NativeLibrary For=\"\" Path=\"libs/glfw3.lib\" /></NativeLibraries>")]
	[InlineData("<NativeLibraries Target=\"windows\"><NativeLibrary For=\"glfw3\" Path=\"libs/glfw3.lib\"><RuntimeFile Path=\" \" /></NativeLibrary></NativeLibraries>")]
	public void Parse_EmptyOrWhitespaceValuesRejected(string nativeXml)
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesInvalidStructure, nativeXml);
	}

	// Parsing: duplicates and ambiguity across blocks

	[Fact]
	public void Parse_DuplicateSelectorRejected_AcrossBlocks()
	{
		AssertRejects(PackageDiagnosticIds.NativeLibrariesDuplicateSelector, """
			<NativeLibraries Target="windows">
			  <NativeLibrary For="glfw3" Path="first/glfw3.lib" />
			</NativeLibraries>
			<NativeLibraries Target="windows">
			  <NativeLibrary For="glfw3" Path="second/glfw3.lib" />
			</NativeLibraries>
			""");
	}

	[Fact]
	public void Parse_DuplicateSelectorRejected_SameTargetDifferentSelectorsAllowed()
	{
		var (groups, _) = ParseXml("""
			<NativeLibraries Target="windows">
			  <NativeLibrary For="glfw3" Path="generic/glfw3.lib" />
			</NativeLibraries>
			<NativeLibraries Target="windows" Arch="x64">
			  <NativeLibrary For="glfw3" Path="x64/glfw3.lib" />
			</NativeLibraries>
			""");

		Assert.Equal(2, groups.Count);
	}

	[Fact]
	public void Parse_IncomparableMappingsRejected_WithConflictingEntries()
	{
		var ex = Assert.Throws<PackageException>(() => ParseXml("""
			<NativeLibraries Target="windows" Arch="x64">
			  <NativeLibrary For="glfw3" Path="x64/glfw3.lib" />
			</NativeLibraries>
			<NativeLibraries Target="windows" Configuration="Debug">
			  <NativeLibrary For="glfw3" Path="debug/glfw3.lib" />
			</NativeLibraries>
			"""));

		Assert.Equal(PackageDiagnosticIds.NativeLibrariesAmbiguous, ex.Code);
		Assert.Contains("glfw3", ex.Message, StringComparison.Ordinal);
		Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	// Parsing: paths

	[Fact]
	public void Parse_PathPreservesCase()
	{
		var (groups, _) = ParseXml("""
			<NativeLibraries Target="windows">
			  <NativeLibrary For="GLFW3" Path="Libs/GLFW3.Lib" />
			</NativeLibraries>
			""");

		var declaration = Assert.Single(Assert.Single(groups).Libraries);
		Assert.Equal("GLFW3", declaration.For);
		Assert.Equal("Libs/GLFW3.Lib", declaration.Path);
	}

	[Fact]
	public void Parse_AbsolutePath_WarnsWithCVLP3107_ButParses()
	{
		var (groups, warnings) = ParseXml("""
			<NativeLibraries Target="linux">
			  <NativeLibrary For="glfw3" Path="/opt/libs/libglfw3.so" />
			</NativeLibraries>
			""");

		Assert.Single(groups);
		Assert.Contains(warnings, warning => warning.Code == PackageDiagnosticIds.NativeLibrariesAbsolutePath);
	}

	[Fact]
	public void Parse_RelativePath_NoPortabilityWarning()
	{
		var (_, warnings) = ParseXml("""
			<NativeLibraries Target="windows">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			</NativeLibraries>
			""");

		Assert.Empty(warnings);
	}

	[Fact]
	public void Parse_MultipleRuntimeFiles_Collected()
	{
		var (groups, _) = ParseXml("""
			<NativeLibraries Target="windows">
			  <NativeLibrary For="glfw3" Path="libs/glfw3.lib">
			    <RuntimeFile Path="bin/glfw3.dll" />
			    <RuntimeFile Path="bin/glfw3-license.txt" />
			  </NativeLibrary>
			</NativeLibraries>
			""");

		var declaration = Assert.Single(Assert.Single(groups).Libraries);
		Assert.Equal(["bin/glfw3.dll", "bin/glfw3-license.txt"], declaration.RuntimeFiles);
	}

	// ProjectManifest.LoadNativeLibraries

	private string WriteProjectFile(string name, string content)
	{
		var directory = Path.Combine(_tempDir, name);
		Directory.CreateDirectory(directory);
		var projectPath = Path.Combine(directory, "Fixture.cvlproj");
		File.WriteAllText(projectPath, content);
		return directory;
	}

	[Fact]
	public void LoadNativeLibraries_ToleratesMissingPackageIdAndVersion()
	{
		// Application projects legitimately omit package identity (CVLP3000/3001 only apply to
		// package manifests); native-group loading must not require them.
		var projectDir = WriteProjectFile("appid-less", """
			<Project Sdk="Cvolo.Sdk">
			  <PropertyGroup>
			    <OutputType>Exe</OutputType>
			    <AssemblyName>App</AssemblyName>
			  </PropertyGroup>
			  <NativeLibraries Target="windows">
			    <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			  </NativeLibraries>
			</Project>
			""");

		var (groups, warnings) = ProjectManifest.LoadNativeLibraries(projectDir);

		var group = Assert.Single(groups);
		Assert.Equal(NativeLibraryTarget.Windows, group.Target);
		Assert.Equal("glfw3", Assert.Single(group.Libraries).For);
		Assert.Empty(warnings);
	}

	[Fact]
	public void LoadNativeLibraries_NoNativeGroups_ReturnsEmpty()
	{
		var projectDir = WriteProjectFile("no-groups", """
			<Project Sdk="Cvolo.Sdk">
			  <PropertyGroup>
			    <OutputType>Exe</OutputType>
			    <AssemblyName>App</AssemblyName>
			  </PropertyGroup>
			</Project>
			""");

		var (groups, warnings) = ProjectManifest.LoadNativeLibraries(projectDir);

		Assert.Empty(groups);
		Assert.Empty(warnings);
	}

	[Fact]
	public void LoadNativeLibraries_ValidationErrors_StillThrow()
	{
		var projectDir = WriteProjectFile("bad-target", """
			<Project Sdk="Cvolo.Sdk">
			  <PropertyGroup>
			    <OutputType>Exe</OutputType>
			    <AssemblyName>App</AssemblyName>
			  </PropertyGroup>
			  <NativeLibraries Target="windwos">
			    <NativeLibrary For="glfw3" Path="libs/glfw3.lib" />
			  </NativeLibraries>
			</Project>
			""");

		var ex = Assert.Throws<PackageException>(() => ProjectManifest.LoadNativeLibraries(projectDir));

		Assert.Equal(PackageDiagnosticIds.NativeLibrariesUnknownTarget, ex.Code);
	}

	[Fact]
	public void LoadNativeLibraries_WalksUpFromSubdirectory()
	{
		var projectDir = WriteProjectFile("walkup", """
			<Project Sdk="Cvolo.Sdk">
			  <PropertyGroup>
			    <OutputType>Exe</OutputType>
			    <AssemblyName>App</AssemblyName>
			  </PropertyGroup>
			  <NativeLibraries Target="linux">
			    <NativeLibrary For="glfw3" Path="libs/libglfw3.so" />
			  </NativeLibraries>
			</Project>
			""");
		var sub = Path.Combine(projectDir, "Deep", "Nested");
		Directory.CreateDirectory(sub);

		var (groups, _) = ProjectManifest.LoadNativeLibraries(sub);

		Assert.Equal(NativeLibraryTarget.Linux, Assert.Single(groups).Target);
	}
}
