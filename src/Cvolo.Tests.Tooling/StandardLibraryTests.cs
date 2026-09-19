using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Proves that the tooling observes the same standard-library universe as the compiler:
/// symbol lookup, diagnostics and namespace-receiver completion resolve std symbols through the
/// real project model (no hardcoded std catalog, no second namespace resolver).
/// </summary>
public sealed class StandardLibraryTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture, int Position) Open(string marked)
	{
		var position = marked.IndexOf('|');
		var source = position >= 0 ? marked.Remove(position, 1) : marked;

		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture, position);
	}

	[Fact]
	public void StandardLibrary_ContributesDocumentsToTheSnapshot()
	{
		const string s = "using System;\nint main() { return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			// The std sources are part of the same universe, discovered (not hardcoded).
			Assert.Contains(x.Snapshot.Documents.Values, d => d.FilePath.EndsWith("System" + Path.DirectorySeparatorChar + "Console.cvl", StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void StdSymbolLookup_ResolvesImportedSymbolWithDefinition()
	{
		const string s = "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.IndexOf("WriteLine", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Function, symbol!.Kind);
			Assert.Contains("WriteLine", symbol.DisplayText);

			// Std is loaded as source, so a real, source-backed definition exists (never fabricated).
			var definitions = x.Snapshot.GetDefinitions(symbol.SymbolId);
			Assert.NotEmpty(definitions);
			foreach (var definition in definitions)
			{
				Assert.True(x.Snapshot.TryGetDocument(definition.DocumentId, out var document));
				Assert.True(File.Exists(document.FilePath));
			}
		}
	}

	[Fact]
	public void StdCall_ProducesNoFalseOverloadDiagnostic()
	{
		const string s = "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			Assert.DoesNotContain(x.Document.GetDiagnostics(), d => d.Message.Contains("Console.WriteLine", StringComparison.Ordinal));
		}
	}

	[Fact]
	public void NamespaceReceiverCompletion_ResolvesThroughUsing()
	{
		const string s = "using System;\nint main() { return Console.|; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var result = x.Document.GetCompletions(x.Position);
			Assert.Contains(result.Candidates, c => c.Label == "WriteLine");
		}
	}

	[Fact]
	public void NestedNamespaceReceiverCompletion_ResolvesFullyQualified()
	{
		const string s = "int main() { return System.Console.|; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var result = x.Document.GetCompletions(x.Position);
			Assert.Contains(result.Candidates, c => c.Label == "WriteLine");
		}
	}

	[Fact]
	public void PartialNamespaceMember_ReplacesPrefixAndOffersMatch()
	{
		const string s = "using System;\nint main() { return Console.Wr|; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var result = x.Document.GetCompletions(x.Position);
			Assert.Contains(result.Candidates, c => c.Label == "WriteLine");
			Assert.Equal(x.Position, result.ReplacementRange.End);
			Assert.Equal(2, result.ReplacementRange.Length);
		}
	}

	[Fact]
	public void UnresolvedNamespaceReceiver_YieldsNoMembersOrFallback()
	{
		const string s = "int main() { return NonexistentNs.|; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var result = x.Document.GetCompletions(x.Position);
			Assert.Empty(result.Candidates);
		}
	}

	[Fact]
	public void SnapshotIsolation_StdUnaffectedByProjectEdit()
	{
		const string s = "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.IndexOf("WriteLine", StringComparison.Ordinal));
			Assert.NotNull(symbol);

			var edited = x.Snapshot.WithDocument(x.Document.Id, SourceText.From("int main() { return 0; }\n"));

			// The old snapshot still resolves the std symbol; the derived snapshot carries the edit.
			Assert.NotEmpty(x.Snapshot.GetDefinitions(symbol!.SymbolId));
			Assert.Equal("int main() { return 0; }\n", edited.GetDocument(x.Document.Id).Text.ToString());
		}
	}
}
