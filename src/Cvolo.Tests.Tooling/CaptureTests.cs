using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Covers the frozen-capture producer/consumer contract: <c>CaptureUniverseV1</c> records every
/// input byte, and <c>BuildSnapshotFromCaptureV1</c> reconstructs an equivalent snapshot with no
/// further file-system access and fresh, generation-owned syntax.
/// </summary>
public sealed class CaptureTests
{
	private static readonly string MainSource = "int main() { return 0; }\n";
	private static readonly string LibSource = "int Helper() { return 42; }\n";

	[Fact]
	public void CaptureUniverse_RecordsEverySourceRead()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainSource), ("Lib.cvl", LibSource));
		var workspace = CvoloWorkspace.Create();
		var reader = new RecordingCompilerInputReader(FileSystemCompilerInputReader.Instance);

		var result = workspace.CaptureUniverseV1(new ToolingCaptureRequest(fixture.ProjectFilePath), reader);

		var captured = Assert.IsType<CaptureResult.Captured>(result);
		Assert.Contains(Path.GetFullPath(fixture.GetPath("Main.cvl")), reader.Reads);
		Assert.Contains(Path.GetFullPath(fixture.GetPath("Lib.cvl")), reader.Reads);
		Assert.Contains(captured.Capture.Sources, s => s.Path == Path.GetFullPath(fixture.GetPath("Main.cvl")));
	}

	[Fact]
	public void CaptureUniverse_RejectsUnsupportedTarget()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainSource));
		var workspace = CvoloWorkspace.Create();

		var result = workspace.CaptureUniverseV1(
			new ToolingCaptureRequest(fixture.ProjectFilePath, RequestedTarget: "aarch64-linux-gnu"),
			FileSystemCompilerInputReader.Instance);

		var unavailable = Assert.IsType<CaptureResult.Unavailable>(result);
		Assert.Equal(CaptureUnavailableReason.UnsupportedTarget, unavailable.Reason);
	}

	[Fact]
	public void Build_RestoresEquivalentSnapshot_WithNoDiskAccess()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainSource), ("Lib.cvl", LibSource));
		var workspace = CvoloWorkspace.Create();
		var reader = new RecordingCompilerInputReader(FileSystemCompilerInputReader.Instance);

		var captured = Assert.IsType<CaptureResult.Captured>(
			workspace.CaptureUniverseV1(new ToolingCaptureRequest(fixture.ProjectFilePath), reader));

		// Remove the project sources after capture: a build must not need them again.
		File.Delete(fixture.GetPath("Main.cvl"));
		File.Delete(fixture.GetPath("Lib.cvl"));

		var build = workspace.BuildSnapshotFromCaptureV1(captured.Capture);

		var ready = Assert.IsType<BuildResult.Ready>(build);
		Assert.True(ready.Project.TryGetDocumentId(fixture.GetPath("Main.cvl"), out var mainId));
		Assert.Equal(MainSource, ready.Snapshot.GetDocument(mainId).Text.ToString());
		Assert.DoesNotContain(ready.Snapshot.GetDocument(mainId).GetDiagnostics(),
			d => d.Severity == DiagnosticSeverity.Error);
	}

	[Fact]
	public void Build_AppliesSourceOverrides()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainSource));
		var workspace = CvoloWorkspace.Create();
		var mainPath = Path.GetFullPath(fixture.GetPath("Main.cvl"));
		const string Overlay = "int main() { return 7; }\n";

		var captured = Assert.IsType<CaptureResult.Captured>(
			workspace.CaptureUniverseV1(
				new ToolingCaptureRequest(fixture.ProjectFilePath, new Dictionary<string, string> { [mainPath] = Overlay }),
				FileSystemCompilerInputReader.Instance));

		var ready = Assert.IsType<BuildResult.Ready>(workspace.BuildSnapshotFromCaptureV1(captured.Capture));

		Assert.True(ready.Project.TryGetDocumentId(mainPath, out var mainId));
		Assert.Equal(Overlay, ready.Snapshot.GetDocument(mainId).Text.ToString());
	}

	[Fact]
	public void Build_ProducesFreshSyntaxObjects_PerGeneration()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainSource));
		var workspace = CvoloWorkspace.Create();

		var captured = Assert.IsType<CaptureResult.Captured>(
			workspace.CaptureUniverseV1(new ToolingCaptureRequest(fixture.ProjectFilePath), FileSystemCompilerInputReader.Instance));

		var first = Assert.IsType<BuildResult.Ready>(workspace.BuildSnapshotFromCaptureV1(captured.Capture));
		var second = Assert.IsType<BuildResult.Ready>(workspace.BuildSnapshotFromCaptureV1(captured.Capture));

		Assert.True(first.Project.TryGetDocumentId(fixture.GetPath("Main.cvl"), out var firstId));
		Assert.True(second.Project.TryGetDocumentId(fixture.GetPath("Main.cvl"), out var secondId));

		Assert.True(first.Snapshot.TryGetParsedUnit(firstId, out var firstUnit));
		Assert.True(second.Snapshot.TryGetParsedUnit(secondId, out var secondUnit));

		Assert.NotNull(firstUnit);
		Assert.NotNull(secondUnit);
		Assert.NotSame(firstUnit, secondUnit);
	}

	[Fact]
	public void Build_PreservesDocumentIds_FromPriorProject()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainSource));
		var workspace = CvoloWorkspace.Create();
		var mainPath = Path.GetFullPath(fixture.GetPath("Main.cvl"));

		var captured = Assert.IsType<CaptureResult.Captured>(
			workspace.CaptureUniverseV1(new ToolingCaptureRequest(fixture.ProjectFilePath), FileSystemCompilerInputReader.Instance));

		var first = Assert.IsType<BuildResult.Ready>(workspace.BuildSnapshotFromCaptureV1(captured.Capture));
		Assert.True(first.Project.TryGetDocumentId(mainPath, out var firstId));

		var second = Assert.IsType<BuildResult.Ready>(workspace.BuildSnapshotFromCaptureV1(captured.Capture, first.Project));
		Assert.True(second.Project.TryGetDocumentId(mainPath, out var secondId));

		Assert.Equal(firstId, secondId);
	}

	[Fact]
	public void Build_ProducesDeterministicCaptureDigest()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainSource));
		var workspace = CvoloWorkspace.Create();

		var first = Assert.IsType<CaptureResult.Captured>(
			workspace.CaptureUniverseV1(new ToolingCaptureRequest(fixture.ProjectFilePath), FileSystemCompilerInputReader.Instance));
		var second = Assert.IsType<CaptureResult.Captured>(
			workspace.CaptureUniverseV1(new ToolingCaptureRequest(fixture.ProjectFilePath), FileSystemCompilerInputReader.Instance));

		Assert.Equal(first.Capture.CaptureDigest, second.Capture.CaptureDigest);
	}

	[Fact]
	public void PrepareAnalysisV1_ReturnsHandleForSnapshot()
	{
		using var fixture = TempProject.Create(("Main.cvl", MainSource));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

		var handle = project.InitialSnapshot.PrepareAnalysisV1();

		Assert.Same(project.InitialSnapshot, handle.Snapshot);
	}
}
