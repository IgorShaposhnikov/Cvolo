using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class ReferencesRenameTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(params (string File, string Source)[] files)
	{
		var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(files[0].File)), fixture);
	}

	[Fact]
	public void References_HonorIncludeDeclarationAndIgnoreSameSpellingShadow()
	{
		const string source =
			"int Twice(int value) { return value + value; }\n" +
			"int Other(int value) { return value; }\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var firstUse = source.IndexOf("value +", StringComparison.Ordinal);
			var symbol = x.Document.GetSymbolAtPosition(firstUse);
			Assert.NotNull(symbol);

			var withoutDeclaration = x.Snapshot.GetReferences(symbol!.SymbolId, includeDeclaration: false);
			Assert.Equal(2, withoutDeclaration.Count);
			Assert.All(withoutDeclaration, r => Assert.False(r.IsDeclaration));

			var withDeclaration = x.Snapshot.GetReferences(symbol!.SymbolId, includeDeclaration: true);
			Assert.Equal(3, withDeclaration.Count);
			Assert.Single(withDeclaration, r => r.IsDeclaration);
			Assert.All(withDeclaration, r => Assert.Equal(x.Document.Id, r.DocumentId));
			Assert.DoesNotContain(withDeclaration, r => r.Span.Start > source.IndexOf("Other", StringComparison.Ordinal));
		}
	}

	[Fact]
	public void References_CrossFileFunctionUseReturnsBothDocuments()
	{
		const string main = "int main() { return Helper(); }\n";
		const string lib = "int Helper() { return 42; }\n";
		var x = Open(("Main.cvl", main), ("Lib.cvl", lib));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(main.IndexOf("Helper", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			var refs = x.Snapshot.GetReferences(symbol!.SymbolId, includeDeclaration: true);
			Assert.Equal(2, refs.Count);
			Assert.Contains(refs, r => r.DocumentId == x.Project.GetDocumentId("Main.cvl") && !r.IsDeclaration);
			Assert.Contains(refs, r => r.DocumentId == x.Project.GetDocumentId("Lib.cvl") && r.IsDeclaration);
		}
	}

	[Fact]
	public void PrepareRename_ReturnsExactOccurrenceAndPlaceholder()
	{
		const string source = "int Twice(int value) { return value + value; }\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var position = source.LastIndexOf("value", StringComparison.Ordinal);
			var preparation = x.Document.PrepareRename(position);
			Assert.NotNull(preparation);
			Assert.Equal("value", preparation!.Placeholder);
			Assert.Equal("value", x.Document.Text.GetText(preparation!.SubjectSpan));
		}
	}

	[Fact]
	public void Rename_CrossFileProducesCompleteSemanticEditSet()
	{
		const string main = "int main() { return Helper(); }\n";
		const string lib = "int Helper() { return 42; }\n";
		var x = Open(("Main.cvl", main), ("Lib.cvl", lib));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(main.IndexOf("Helper", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			var success = Assert.IsType<RenameSuccess>(x.Snapshot.RenameSymbol(symbol!.SymbolId, "Compute"));
			Assert.Equal(2, success.Edits.Count);
			Assert.All(success.Edits, edit => Assert.Equal("Compute", edit.NewText));
			Assert.Contains(success.Edits, edit => edit.DocumentId == x.Project.GetDocumentId("Main.cvl"));
			Assert.Contains(success.Edits, edit => edit.DocumentId == x.Project.GetDocumentId("Lib.cvl"));
		}
	}

	[Fact]
	public void Rename_InvalidNameAndCollisionFailWithoutPartialEdits()
	{
		const string source =
			"int First() { return 1; }\n" +
			"int Second() { return First(); }\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(source.LastIndexOf("First", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			Assert.IsType<RenameFailure>(x.Snapshot.RenameSymbol(symbol!.SymbolId, "not-valid!"));
			Assert.IsType<RenameFailure>(x.Snapshot.RenameSymbol(symbol!.SymbolId, "Second"));
		}
	}

	[Fact]
	public void Rename_RejectsSameSignatureCollisionButAllowsDistinctOverload()
	{
		const string source =
			"int Existing(int value) { return value; }\n" +
			"int Target(int value) { return value + 1; }\n" +
			"int Existing() { return 0; }\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(source.IndexOf("Target", StringComparison.Ordinal));
			Assert.NotNull(symbol);

			var collision = Assert.IsType<RenameFailure>(x.Snapshot.RenameSymbol(symbol!.SymbolId, "Existing"));
			Assert.Contains("same signature", collision.Message, StringComparison.OrdinalIgnoreCase);

			const string overloadSource =
				"int Existing() { return 0; }\n" +
				"int Target(int value) { return value + 1; }\n";
			var y = Open(("Main.cvl", overloadSource));
			using (y.Fixture)
			{
				var overloadSymbol = y.Document.GetSymbolAtPosition(overloadSource.IndexOf("Target", StringComparison.Ordinal));
				Assert.NotNull(overloadSymbol);
				Assert.IsType<RenameSuccess>(y.Snapshot.RenameSymbol(overloadSymbol!.SymbolId, "Existing"));
			}
		}
	}

	[Fact]
	public void Rename_RejectsCollisionAfterEarlierNameWasAlreadyChanged()
	{
		const string source =
			"int CalculateShared(int value) { return value * 2; }\n" +
			"int ExistingName(int value) { return value; }\n" +
			"int ExistingOverload() { return 0; }\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var collisionSource = x.Document.GetSymbolAtPosition(source.IndexOf("ExistingName", StringComparison.Ordinal));
			Assert.NotNull(collisionSource);
			var collision = Assert.IsType<RenameFailure>(x.Snapshot.RenameSymbol(collisionSource!.SymbolId, "CalculateShared"));
			Assert.Contains("same signature", collision.Message, StringComparison.OrdinalIgnoreCase);

			var overloadSource = x.Document.GetSymbolAtPosition(source.IndexOf("ExistingOverload", StringComparison.Ordinal));
			Assert.NotNull(overloadSource);
			Assert.IsType<RenameSuccess>(x.Snapshot.RenameSymbol(overloadSource!.SymbolId, "CalculateShared"));
		}
	}

	[Fact]
	public void Rename_NoOpSucceedsAndSnapshotRemainsUnchanged()
	{
		const string source = "int Helper() { return 1; }\nint main() { return Helper(); }\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(source.LastIndexOf("Helper", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			var success = Assert.IsType<RenameSuccess>(x.Snapshot.RenameSymbol(symbol!.SymbolId, "Helper"));
			Assert.Empty(success.Edits);
			Assert.Equal(source, x.Document.Text.ToString());
		}
	}

	[Fact]
	public void ForeignSnapshotSymbolIdIsRejected()
	{
		const string source = "int Helper() { return 1; }\nint main() { return Helper(); }\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(source.LastIndexOf("Helper", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			var derived = x.Snapshot.WithDocument(x.Document.Id, SourceText.From(source.Replace("return 1", "return 2", StringComparison.Ordinal)));
			Assert.Empty(derived.GetReferences(symbol!.SymbolId, includeDeclaration: true));
			Assert.IsType<RenameFailure>(derived.RenameSymbol(symbol!.SymbolId, "Compute"));
		}
	}
}
