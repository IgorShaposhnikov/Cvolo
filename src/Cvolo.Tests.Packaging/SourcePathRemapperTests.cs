using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class SourcePathRemapperTests
{
	[Fact]
	public void Map_ProjectSource_UsesStableProjectPrefix()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-remap", "checkout-a", "App");
		var source = Path.Combine(root, "Sub", "Main.cvl");

		var mapped = SourcePathRemapper.Map(source, root);

		Assert.Equal("/project/Sub/Main.cvl", mapped);
		Assert.DoesNotContain(Path.GetTempPath().Replace('\\', '/'), mapped, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Map_StandardLibrarySource_DoesNotExposeCompilerInstallPath()
	{
		var root = Path.Combine(Path.GetTempPath(), "compiler-a", "bin", "Debug", "net10.0");
		var source = Path.Combine(root, "libraries", "System", "Console.cvl");

		var mapped = SourcePathRemapper.Map(source, Path.Combine(Path.GetTempPath(), "project"));

		Assert.Equal("/stdlib/System/Console.cvl", mapped);
		Assert.DoesNotContain("compiler-a", mapped, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Map_ProjectReferenceSource_UsesReferencedProjectIdentity()
	{
		var workspace = Path.Combine(Path.GetTempPath(), "cvolo-remap", "workspace-a");
		var app = Path.Combine(workspace, "App");
		var library = Path.Combine(workspace, "MathLib");
		var projectReference = Path.Combine(library, "MathLib.cvlproj");
		var source = Path.Combine(library, "Models", "Pair.cvl");

		var mapped = SourcePathRemapper.Map(source, app, [projectReference]);

		Assert.Equal("/project-ref/MathLib/Models/Pair.cvl", mapped);
		Assert.DoesNotContain(workspace.Replace('\\', '/'), mapped, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Map_SameLogicalProjectInDifferentRoots_ProducesSameVirtualPath()
	{
		var rootA = Path.Combine(Path.GetTempPath(), "machine-a", "repo", "App");
		var rootB = Path.Combine(Path.GetTempPath(), "machine-b", "other", "repo", "App");

		var mappedA = SourcePathRemapper.Map(Path.Combine(rootA, "src", "Main.cvl"), rootA);
		var mappedB = SourcePathRemapper.Map(Path.Combine(rootB, "src", "Main.cvl"), rootB);

		Assert.Equal(mappedA, mappedB);
		Assert.Equal("/project/src/Main.cvl", mappedA);
	}
}
