using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Staleness and identity boundaries for provisional editing contexts: an incomplete
/// declaration must never leak a committed symbol, must follow cross-file changes, must not
/// be conflated with a neighbouring declaration, and imported-extern colouring must be
/// dropped once the declaration changes.
/// </summary>
public sealed class EditorStalenessMatrixTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentId Id) Open(params (string Name, string Source)[] files)
	{
		using var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var id = project.GetDocumentId(files[0].Name);
		return (project, project.InitialSnapshot, id);
	}

	private static int At(string source, string needle, int delta = 0) => source.IndexOf(needle, StringComparison.Ordinal) + delta;

	[Fact]
	public void ProvisionalLocal_NameDoesNotSurviveItsOwnDeclarationBeingRemoved()
	{
		const string source =
			"struct Window { int Width; }\n" +
			"int main()\n" +
			"{\n" +
			"    Window windo = Window { Width = 1 };\n" +
			"    return 0;\n" +
			"}\n";
		var (_, snapshot, id) = Open(("Main.cvl", source));
		var before = snapshot.GetDocument(id);
		var position = At(source, "windo", 2);
		Assert.NotNull(before.GetSymbolAtPosition(position));

		// Edit that deletes the local declaration entirely.
		var edited = snapshot.WithDocument(id, SourceText.From(
			"struct Window { int Width; }\n" +
			"int main()\n" +
			"{\n" +
			"    return 0;\n" +
			"}\n"));
		var after = edited.GetDocument(id);
		Assert.Null(after.GetSymbolAtPosition(position));
	}

	[Fact]
	public void ProvisionalType_FollowsAChangeToTheDefiningOtherFile()
	{
		const string main =
			"int main()\n" +
			"{\n" +
			"    Window windo = Window { Width = 1 };\n" +
			"    return 0;\n" +
			"}\n";
		const string lib = "public struct Window { int Width; }\n";
		var (project, snapshot, mainId) = Open(("Main.cvl", main), ("Lib.cvl", lib));
		var document = snapshot.GetDocument(mainId);
		var libId = project.GetDocumentId("Lib.cvl");

		var typePosition = At(main, "Window { Width = 1 }", 2);
		var typeSymbol = document.GetSymbolAtPosition(typePosition);
		Assert.NotNull(typeSymbol);
		Assert.Contains(snapshot.GetDefinitions(typeSymbol!.SymbolId), definition => definition.DocumentId == libId);

		// Rename the defining type in the other file: the cross-file identity must not persist.
		var edited = snapshot.WithDocument(libId, SourceText.From("public struct Renamed { int Width; }\n"));
		var afterSymbol = edited.GetDocument(mainId).GetSymbolAtPosition(typePosition);
		var afterDefinitions = afterSymbol is null
			? []
			: edited.GetDefinitions(afterSymbol.SymbolId);
		Assert.DoesNotContain(afterDefinitions, definition => definition.DocumentId == libId);
	}

	[Fact]
	public void AdjacentSameTypeDeclarations_DoNotConflateTheirNames()
	{
		const string source =
			"struct Window { int Width; }\n" +
			"int main()\n" +
			"{\n" +
			"    Window w;\n" +
			"    Window windo = Window { Width = 1 };\n" +
			"    return w.Width + windo.Width;\n" +
			"}\n";
		var (_, snapshot, id) = Open(("Main.cvl", source));
		var document = snapshot.GetDocument(id);
		var wSymbol = document.GetSymbolAtPosition(At(source, "Window w;", "Window w;".Length - 1));
		var windoSymbol = document.GetSymbolAtPosition(At(source, "windo", 2));
		Assert.NotNull(wSymbol);
		Assert.NotNull(windoSymbol);
		Assert.NotEqual(wSymbol!.SymbolId, windoSymbol!.SymbolId);

		var wReferences = snapshot.GetReferences(wSymbol.SymbolId, includeDeclaration: false);
		Assert.DoesNotContain(wReferences, reference => reference.Span.Start == At(source, "windo", 2));
	}

	[Fact]
	public void ImportedExternStyling_IsDroppedWhenTheDeclarationChanges()
	{
		const string source =
			"extern int abs(int x);\n" +
			"int main() { return abs(-1); }\n";
		var (_, snapshot, id) = Open(("Main.cvl", source));
		var document = snapshot.GetDocument(id);
		var before = document.GetSemanticTokens()
			.Where(token => document.Text.GetText(token.Span) == "abs")
			.ToArray();
		Assert.NotEmpty(before);
		Assert.All(before, token => Assert.True(token.Modifiers.HasFlag(SemanticTokenModifiers.ExternFunction)));

		// Drop the import: the stale extern origin must not keep colouring the old uses.
		var edited = snapshot.WithDocument(id, SourceText.From(
			"int main() { return 0; }\n"));
		var afterDocument = edited.GetDocument(id);
		Assert.DoesNotContain(
			afterDocument.GetSemanticTokens(),
			token => afterDocument.Text.GetText(token.Span) == "abs");
	}
}
