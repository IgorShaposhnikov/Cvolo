using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Cvolo.Compiler.Tooling;
using Cvolo.Core.Packages;
using Cvolo.Packaging;

namespace Cvolo.Tests.Tooling;

public sealed class LooseWorkspaceLibraryPathTests
{
	[Fact]
	public void LooseDirectory_AcceptsExplicitLibraryDirectory()
	{
		var root = CreateRoot();
		var libraries = Path.Combine(root, "libs");
		Directory.CreateDirectory(libraries);
		try
		{
			File.WriteAllText(Path.Combine(root, "Main.cvl"), "int main() { return 0; }\n");

			var project = CvoloWorkspace.Create().OpenProject(root, ["libs"]);

			Assert.True(project.TryGetDocumentId("Main.cvl", out _));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ExplicitCvlib_ContributesSemanticApiToLooseWorkspace()
	{
		var root = CreateRoot();
		var libraries = Path.Combine(root, "libs");
		Directory.CreateDirectory(libraries);
		try
		{
			const string source = "using LooseLib;\nint main() { return Ping(1); }\n";
			File.WriteAllText(Path.Combine(root, "Main.cvl"), source);
			WriteApiOnlyLibrary(Path.Combine(libraries, "LooseLib.cvlib"));

			var project = CvoloWorkspace.Create().OpenProject(root, ["libs"]);
			var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
			var position = source.IndexOf("Ping", StringComparison.Ordinal) + 1;
			SymbolLookupResult? symbol = document.GetSymbolAtPosition(position);

			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Function, symbol!.Kind);
			Assert.Equal("Ping", symbol.Name);
			Assert.DoesNotContain(document.GetDiagnostics(), diagnostic =>
				diagnostic.Message.Contains("Ping", StringComparison.Ordinal));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ProjectPackageReferences_RestoreFromCvlprojAndFeed_WithoutLibraryPaths()
	{
		var root = CreateRoot();
		var app = Path.Combine(root, "app");
		var feed = Path.Combine(root, "feed");
		var cache = new PackageCache(Path.Combine(root, "cache"));
		Directory.CreateDirectory(app);
		Directory.CreateDirectory(feed);
		try
		{
			WriteInstallableApiLibrary(
				Path.Combine(feed, "GLFW.1.0.0.cvlib"),
				"GLFW",
				"GLFW",
				new PackageApiFunction { Name = "Init", ReturnType = "int" });
			WriteInstallableApiLibrary(
				Path.Combine(feed, "OpenGL.1.0.0.cvlib"),
				"OpenGL",
				"OpenGL",
				new PackageApiFunction
				{
					Name = "ClearColor",
					ReturnType = "void",
					Parameters =
					[
						new PackageApiParameter("float", "red"),
						new PackageApiParameter("float", "green"),
						new PackageApiParameter("float", "blue"),
						new PackageApiParameter("float", "alpha"),
					],
				});

			File.WriteAllText(Path.Combine(app, "OpenGLPackageApp.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>OpenGLPackageApp</AssemblyName>
    <PackageId>OpenGLPackageApp</PackageId>
    <Version>1.0.0</Version>
    <LocalFeed>../feed</LocalFeed>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="GLFW" Version="1.0.0" />
    <PackageReference Include="OpenGL" Version="1.0.0" />
  </ItemGroup>
</Project>
""");

			const string source =
				"using GLFW;\n" +
				"using OpenGL;\n" +
				"namespace App;\n" +
				"int main() { int value = Init(); ClearColor(0.1f, 0.2f, 0.3f, 1.0f); return value; }\n";
			File.WriteAllText(Path.Combine(app, "Main.cvl"), source);

			// No libraryPaths: the .cvlproj + LocalFeed/PackageReference graph is authoritative.
			var project = CvoloWorkspace.Create().OpenProject(app, [], cache);
			var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

			Assert.True(File.Exists(Path.Combine(app, "cvolo.lock.json")));
			var installer = new PackageInstaller(cache);
			Assert.True(installer.IsInstalled("GLFW", "1.0.0"));
			Assert.True(installer.IsInstalled("OpenGL", "1.0.0"));

			var init = document.GetSymbolAtPosition(source.IndexOf("Init", StringComparison.Ordinal) + 1);
			var clearColor = document.GetSymbolAtPosition(source.IndexOf("ClearColor", StringComparison.Ordinal) + 1);

			Assert.NotNull(init);
			Assert.Equal(ToolingSymbolKind.Function, init!.Kind);
			Assert.Equal("Init", init.Name);
			Assert.NotNull(clearColor);
			Assert.Equal(ToolingSymbolKind.Function, clearColor!.Kind);
			Assert.Equal("ClearColor", clearColor.Name);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void LooseDirectory_DoesNotAbsorbNestedProjectSources()
	{
		var root = CreateRoot();
		var nestedProject = Path.Combine(root, "app");
		Directory.CreateDirectory(nestedProject);
		try
		{
			File.WriteAllText(Path.Combine(root, "Main.cvl"), "int main() { return 0; }\n");
			File.WriteAllText(Path.Combine(nestedProject, "App.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
  </PropertyGroup>
</Project>
""");
			File.WriteAllText(Path.Combine(nestedProject, "Nested.cvl"), "int nested() { return 1; }\n");

			var project = CvoloWorkspace.Create().OpenProject(root);

			Assert.True(project.TryGetDocumentId("Main.cvl", out _));
			Assert.False(project.TryGetDocumentId(Path.Combine("app", "Nested.cvl"), out _));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void LooseDirectory_DoesNotRestoreAncestorProjectManifest()
	{
		var root = CreateRoot();
		var loose = Path.Combine(root, "loose");
		Directory.CreateDirectory(loose);
		try
		{
			File.WriteAllText(Path.Combine(root, "App.cvlproj"), "not a manifest");
			File.WriteAllText(Path.Combine(loose, "Main.cvl"), "int main() { return 0; }\n");

			var project = CvoloWorkspace.Create().OpenProject(loose);

			Assert.True(project.TryGetDocumentId("Main.cvl", out _));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ExplicitLibraryFile_MustUseCvlibExtension()
	{
		var root = CreateRoot();
		try
		{
			File.WriteAllText(Path.Combine(root, "Main.cvl"), "int main() { return 0; }\n");
			var invalidLibrary = Path.Combine(root, "Invalid.txt");
			File.WriteAllText(invalidLibrary, "not a library");

			Assert.Throws<ArgumentException>(() =>
				CvoloWorkspace.Create().OpenProject(root, [invalidLibrary]));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ExplicitLibraryFile_MalformedArchive_ThrowsPackageException()
	{
		var root = CreateRoot();
		try
		{
			File.WriteAllText(Path.Combine(root, "Main.cvl"), "int main() { return 0; }\n");
			var malformedLibrary = Path.Combine(root, "Malformed.cvlib");
			File.WriteAllText(malformedLibrary, "not a library");

			Assert.Throws<PackageException>(() =>
				CvoloWorkspace.Create().OpenProject(root, [malformedLibrary]));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ExplicitLibraries_MultipleVersionsOfSamePackage_AreRejected()
	{
		var root = CreateRoot();
		var libraries = Path.Combine(root, "libs");
		Directory.CreateDirectory(libraries);
		try
		{
			File.WriteAllText(Path.Combine(root, "Main.cvl"), "int main() { return 0; }\n");
			WriteInstallableApiLibrary(
				Path.Combine(libraries, "DuplicateLib.1.cvlib"),
				"DuplicateLib",
				"DuplicateLib",
				new PackageApiFunction { Name = "Value", ReturnType = "int" },
				"1.0.0");
			WriteInstallableApiLibrary(
				Path.Combine(libraries, "DuplicateLib.2.cvlib"),
				"DuplicateLib",
				"DuplicateLib",
				new PackageApiFunction { Name = "Value", ReturnType = "int" },
				"2.0.0");

			Assert.Throws<InvalidOperationException>(() =>
				CvoloWorkspace.Create().OpenProject(root, ["libs"]));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ExplicitLibraryPath_Missing_ThrowsFileNotFoundException()
	{
		var root = CreateRoot();
		try
		{
			File.WriteAllText(Path.Combine(root, "Main.cvl"), "int main() { return 0; }\n");
			var missing = Path.Combine(root, "libs", "Missing.cvlib");

			Assert.Throws<FileNotFoundException>(() =>
				CvoloWorkspace.Create().OpenProject(root, [missing]));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-tooling-loose", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void WriteInstallableApiLibrary(
		string path,
		string packageId,
		string namespaceName,
		PackageApiFunction function,
		string version = "1.0.0")
	{
		var manifestJson = JsonSerializer.Serialize(new
		{
			Format = "cvlib.slice-manifest.v1",
			PackageId = packageId,
			Version = version,
			Dependencies = Array.Empty<object>(),
			Slices = new[]
			{
				new
				{
					Triple = TargetTriple.HostTriple(),
					Sector2 = new { Offset = 0UL, Length = 0UL },
					Sector3 = new { Offset = 0UL, Length = 1UL },
				},
			},
		});
		var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
		var api = new PackageApiMetadata
		{
			Units =
			[
				new PackageApiUnit
				{
					Namespace = namespaceName,
					Functions = [function],
				},
			],
		}.Serialize();

		var sector1 = new byte[4 + manifestBytes.Length + api.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(sector1, (uint)manifestBytes.Length);
		manifestBytes.CopyTo(sector1, 4);
		api.CopyTo(sector1, 4 + manifestBytes.Length);

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, sector1);
		writer.SetSector(3, new byte[] { 0 });
		writer.WriteUnsigned(path);
	}

	private static void WriteApiOnlyLibrary(string path)
	{
		var manifestJson = JsonSerializer.Serialize(new
		{
			Format = "cvlib.slice-manifest.v1",
			PackageId = "LooseLib",
			Version = "1.0.0",
			Dependencies = Array.Empty<object>(),
			Slices = Array.Empty<object>(),
		});
		var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
		var api = new PackageApiMetadata
		{
			Units =
			[
				new PackageApiUnit
				{
					Namespace = "LooseLib",
					Functions =
					[
						new PackageApiFunction
						{
							Name = "Ping",
							ReturnType = "int",
							Parameters = [new PackageApiParameter("int", "value")],
						},
					],
				},
			],
		}.Serialize();

		var sector1 = new byte[4 + manifestBytes.Length + api.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(sector1, (uint)manifestBytes.Length);
		manifestBytes.CopyTo(sector1, 4);
		api.CopyTo(sector1, 4 + manifestBytes.Length);

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, sector1);
		writer.WriteUnsigned(path);
	}
}
