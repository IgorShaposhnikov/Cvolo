using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class SnapshotTests
{
	private const string OriginalText = "int main() {\n    return 0;\n}\n";
	private const string EditedText = "int main() {\n    val int x = 10;\n    return x;\n}\n";

	[Fact]
	public void WithDocument_DoesNotMutateBaseSnapshot()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText), ("Lib.cvl", "int Helper() { return 42; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var baseSnapshot = project.InitialSnapshot;
		var docId = project.GetDocumentId("Main.cvl");

		var derived = baseSnapshot.WithDocument(docId, SourceText.From(EditedText));

		Assert.Equal(OriginalText, baseSnapshot.GetDocument(docId).Text.ToString());
		Assert.Equal(EditedText, derived.GetDocument(docId).Text.ToString());
	}

	[Fact]
	public void WithDocument_PreservesProjectIdAndDocumentIds()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText), ("Lib.cvl", "int Helper() { return 42; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var baseSnapshot = project.InitialSnapshot;
		var docId = baseSnapshot.DocumentIds[0];

		var derived = baseSnapshot.WithDocument(docId, SourceText.From(EditedText));

		Assert.Equal(baseSnapshot.ProjectId, derived.ProjectId);
		Assert.Equal(baseSnapshot.DocumentIds, derived.DocumentIds);
		Assert.Equal(docId, derived.GetDocument(docId).Id);
	}

	[Fact]
	public void WithDocument_UnchangedDocuments_KeepTheirText()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText), ("Lib.cvl", "int Helper() { return 42; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var baseSnapshot = project.InitialSnapshot;
		var mainId = project.GetDocumentId("Main.cvl");
		var libId = project.GetDocumentId("Lib.cvl");

		var derived = baseSnapshot.WithDocument(mainId, SourceText.From(EditedText));

		Assert.Equal(baseSnapshot.GetDocument(libId).Text.ToString(),
			derived.GetDocument(libId).Text.ToString());
	}

	[Fact]
	public void WithDocument_UnknownOrForeignId_ThrowsKeyNotFoundException()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var otherWorkspaceProject = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var foreignId = otherWorkspaceProject.InitialSnapshot.DocumentIds[0];

		var unknownId = default(DocumentId);

		Assert.Throws<KeyNotFoundException>(() => snapshot.WithDocument(unknownId, SourceText.From(EditedText)));
		Assert.Throws<KeyNotFoundException>(() => snapshot.WithDocument(foreignId, SourceText.From(EditedText)));
	}

	[Fact]
	public void WithDocument_NullSource_ThrowsArgumentNullException()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var docId = snapshot.DocumentIds[0];

		Assert.Throws<ArgumentNullException>(() => snapshot.WithDocument(docId, null!));
	}

	[Fact]
	public void GetDocument_UnknownOrForeignId_ThrowsKeyNotFoundException()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var otherWorkspaceProject = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var foreignId = otherWorkspaceProject.InitialSnapshot.DocumentIds[0];

		Assert.Throws<KeyNotFoundException>(() => snapshot.GetDocument(default));
		Assert.Throws<KeyNotFoundException>(() => snapshot.GetDocument(foreignId));
	}

	[Fact]
	public void TryGetDocument_UnknownOrForeignId_ReturnsFalse()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var otherWorkspaceProject = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var foreignId = otherWorkspaceProject.InitialSnapshot.DocumentIds[0];

		Assert.False(snapshot.TryGetDocument(default, out _));
		Assert.False(snapshot.TryGetDocument(foreignId, out _));
	}

	[Fact]
	public void GetDocument_AndTryGetDocument_ReturnContainedDocuments()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText), ("Lib.cvl", "int Helper() { return 42; }\n"));
		var snapshot = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath).InitialSnapshot;

		foreach (var docId in snapshot.DocumentIds)
		{
			Assert.NotNull(snapshot.GetDocument(docId));
			Assert.True(snapshot.TryGetDocument(docId, out var document));
			Assert.Equal(docId, document.Id);
		}
	}

	[Fact]
	public void SequentialEdits_ProduceIndependentSnapshots()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var s0 = project.InitialSnapshot;
		var docId = project.GetDocumentId("Main.cvl");

		var s1 = s0.WithDocument(docId, SourceText.From(EditedText));
		var s2 = s1.WithDocument(docId, SourceText.From("int main() { return 123; }\n"));

		Assert.Equal(OriginalText, s0.GetDocument(docId).Text.ToString());
		Assert.Equal(EditedText, s1.GetDocument(docId).Text.ToString());
		Assert.Equal("int main() { return 123; }\n", s2.GetDocument(docId).Text.ToString());
	}

	[Fact]
	public void Snapshots_AreNotDisposable()
	{
		var snapshotType = typeof(ProjectSnapshot);
		Assert.False(typeof(IDisposable).IsAssignableFrom(snapshotType));
		Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(snapshotType));

		var documentType = typeof(DocumentSnapshot);
		Assert.False(typeof(IDisposable).IsAssignableFrom(documentType));
		Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(documentType));
	}
}
