using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class WorkspaceTests
{
	[Fact]
	public void OpenProject_DiscoversDocuments_AndBuildsInitialSnapshot()
	{
		using var fixture = TempProject.Create(
			("Main.cvl", "int main() { return 0; }\n"),
			("Lib.cvl", "int Helper() { return 42; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

		Assert.Equal(Path.GetFullPath(fixture.ProjectFilePath), project.ProjectPath);
		// The universe includes the project files and the standard library.
		Assert.True(project.InitialSnapshot.DocumentIds.Count >= 2);
		Assert.Equal(project.InitialSnapshot.DocumentIds.Count, project.InitialSnapshot.Documents.Count);
		Assert.True(project.TryGetDocumentId("Main.cvl", out _));
		Assert.True(project.TryGetDocumentId("Lib.cvl", out _));

		foreach (var document in project.InitialSnapshot.Documents.Values)
		{
			Assert.True(Path.IsPathRooted(document.FilePath));
			Assert.True(document.Text.Length > 0);
			Assert.True(File.Exists(document.FilePath));
		}
	}

	[Fact]
	public void TryGetDocumentId_ResolvesRelative_AndAbsolutePaths()
	{
		using var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

		Assert.True(project.TryGetDocumentId("Main.cvl", out var relativeId));

		var absolute = fixture.GetPath("Main.cvl");
		Assert.True(project.TryGetDocumentId(absolute, out var absoluteId));
		Assert.Equal(relativeId, absoluteId);

		Assert.True(project.TryGetDocumentId(Path.Combine("sub", "..", "Main.cvl"), out var id));
		Assert.Equal(relativeId, id);
	}

	[Fact]
	public void TryGetDocumentId_IsCaseInsensitive_OnWindowsOnly()
	{
		using var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

		var result = project.TryGetDocumentId("MAIN.CVL", out _);

		if (OperatingSystem.IsWindows())
			Assert.True(result);
		else
			Assert.False(result);
	}

	[Fact]
	public void TryGetDocumentId_UnknownPath_ReturnsFalse()
	{
		using var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

		Assert.False(project.TryGetDocumentId("Missing.cvl", out _));
	}

	[Fact]
	public void TryGetDocumentId_NullPath_ThrowsArgumentNullException()
	{
		using var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

		Assert.Throws<ArgumentNullException>(() => project.TryGetDocumentId(null!, out _));
	}

	[Fact]
	public void OpenProject_NullPath_ThrowsArgumentNullException()
	{
		var workspace = CvoloWorkspace.Create();

		Assert.Throws<ArgumentNullException>(() => workspace.OpenProject(null!));
	}

	[Fact]
	public void OpenProject_UnknownPath_Throws()
	{
		var workspace = CvoloWorkspace.Create();
		var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.cvlproj");

		Assert.Throws<FileNotFoundException>(() => workspace.OpenProject(missing));
	}

	[Fact]
	public void OpenProject_Directory_WithSingleCvlproj_Works()
	{
		using var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(Path.GetDirectoryName(fixture.ProjectFilePath)!);

		Assert.True(project.TryGetDocumentId("Main.cvl", out _));
	}

	[Fact]
	public void OpenProject_Directory_WithoutCvlproj_GlobsCvlFiles()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-tooling-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			File.WriteAllText(Path.Combine(root, "A.cvl"), "int main() { return 0; }\n");

			var project = CvoloWorkspace.Create().OpenProject(root);

			Assert.True(project.TryGetDocumentId("A.cvl", out _));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void OpenProject_SingleCvlFile_Works()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-tooling-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var file = Path.Combine(root, "Single.cvl");
			File.WriteAllText(file, "int main() { return 0; }\n");

			var project = CvoloWorkspace.Create().OpenProject(file);

			Assert.True(project.TryGetDocumentId("Single.cvl", out _));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void StandardLibrary_IsLoaded_ByDefault()
	{
		using var fixture = TempProject.Create(
			("Main.cvl", "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n"));

		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

		// The standard library is part of the project universe by default, and its API resolves.
		Assert.True(project.InitialSnapshot.DocumentIds.Count > 1);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		Assert.DoesNotContain(document.GetDiagnostics(), d => d.Message.Contains("Console.WriteLine", StringComparison.Ordinal));
	}
}
