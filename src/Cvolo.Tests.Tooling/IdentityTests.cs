using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class IdentityTests
{
	private const string OriginalText = "int main() {\n    return 0;\n}\n";
	private const string EditedText = "int main() {\n    return 1;\n}\n";

	[Fact]
	public void DocumentId_IsStable_AcrossDerivedSnapshots()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var baseSnapshot = project.InitialSnapshot;
		var docId = baseSnapshot.DocumentIds[0];

		var derived = baseSnapshot.WithDocument(docId, SourceText.From(EditedText));
		var further = derived.WithDocument(docId, SourceText.From(EditedText.Replace("1", "2")));

		Assert.Equal(docId, derived.GetDocument(docId).Id);
		Assert.Equal(docId, further.GetDocument(docId).Id);
	}

	[Fact]
	public void ProjectId_IsStable_AcrossDerivedSnapshots()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var baseSnapshot = project.InitialSnapshot;

		var derived = baseSnapshot.WithDocument(
			baseSnapshot.DocumentIds[0], SourceText.From(EditedText));

		Assert.Equal(project.Id, baseSnapshot.ProjectId);
		Assert.Equal(project.Id, derived.ProjectId);
	}

	[Fact]
	public void Identities_FromDifferentWorkspaceSessions_DoNotAlias()
	{
		using var leftFixture = TempProject.Create(
			("A.cvl", OriginalText),
			("B.cvl", "int Helper() { return 42; }\n"));
		using var rightFixture = TempProject.Create(
			("A.cvl", EditedText),
			("B.cvl", "int Helper() { return 7; }\n"));

		var leftWorkspace = CvoloWorkspace.Create();
		var rightWorkspace = CvoloWorkspace.Create();
		var leftProject = leftWorkspace.OpenProject(leftFixture.ProjectFilePath);
		var rightProject = rightWorkspace.OpenProject(rightFixture.ProjectFilePath);

		var leftDocId = leftProject.InitialSnapshot.DocumentIds[0];
		var rightDocId = rightProject.InitialSnapshot.DocumentIds[0];

		Assert.NotEqual(leftDocId, rightDocId);
		Assert.NotEqual(leftProject.Id, rightProject.Id);
	}

	[Fact]
	public void ForeignDocumentId_CannotAddressRightWorkspaceDocument()
	{
		using var leftFixture = TempProject.Create(("A.cvl", OriginalText));
		using var rightFixture = TempProject.Create(("A.cvl", EditedText));

		var left = CvoloWorkspace.Create().OpenProject(leftFixture.ProjectFilePath);
		var right = CvoloWorkspace.Create().OpenProject(rightFixture.ProjectFilePath);

		var leftDocId = left.InitialSnapshot.DocumentIds[0];
		var rightSnapshot = right.InitialSnapshot;

		Assert.Throws<KeyNotFoundException>(() => rightSnapshot.GetDocument(leftDocId));
		Assert.False(rightSnapshot.TryGetDocument(leftDocId, out _));
		Assert.Throws<KeyNotFoundException>(() => rightSnapshot.WithDocument(leftDocId, SourceText.From(OriginalText)));
	}

	[Fact]
	public void DocumentId_CanBeUsedAsDictionaryKey_AcrossSnapshots()
	{
		using var fixture = TempProject.Create(("Main.cvl", OriginalText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.InitialSnapshot.DocumentIds[0];
		var derived = project.InitialSnapshot.WithDocument(docId, SourceText.From(EditedText));

		var map = new Dictionary<DocumentId, string>
		{
			[project.InitialSnapshot.GetDocument(docId).Id] = "base",
			[derived.GetDocument(docId).Id] = "derived"
		};

		Assert.Single(map);
		Assert.True(map.TryGetValue(docId, out var value));
		Assert.Equal("derived", value);
	}
}
