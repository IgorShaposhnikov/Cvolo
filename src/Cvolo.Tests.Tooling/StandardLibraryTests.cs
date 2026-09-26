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
	public void Core_ContributesDocumentsWithoutUsingSystem()
	{
		const string s = "int main() { return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			Assert.Contains(x.Snapshot.Documents.Values, d => d.FilePath.EndsWith(Path.Combine("Core", "System", "Option.cvl"), StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void NoStdImport_DoesNotContributeConsoleDocument()
	{
		const string s = "int main() { return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			Assert.DoesNotContain(x.Snapshot.Documents.Values, d => d.FilePath.EndsWith(Path.Combine("Std", "System", "Console.cvl"), StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void UsingSystem_ContributesRequiredStdDocument()
	{
		const string s = "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			Assert.Contains(x.Snapshot.Documents.Values, d => d.FilePath.EndsWith(Path.Combine("Std", "System", "Console.cvl"), StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void OpeningStandardLibrarySource_DoesNotDuplicateDeclarations()
	{
		var fixture = TempProject.Create(("Main.cvl", "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n"));
		using (fixture)
		{
			var reference = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var consolePath = reference.InitialSnapshot.Documents.Values
				.Select(document => document.FilePath)
				.Single(path => path.EndsWith(Path.Combine("Std", "System", "Console.cvl"), StringComparison.OrdinalIgnoreCase));

			var project = CvoloWorkspace.Create().OpenProject(consolePath);
			var snapshot = project.InitialSnapshot;
			var consoleDocuments = snapshot.Documents.Values
				.Where(document => string.Equals(document.FilePath, consolePath, StringComparison.OrdinalIgnoreCase))
				.ToList();

			var consoleDocument = Assert.Single(consoleDocuments);
			Assert.DoesNotContain(consoleDocument.GetDiagnostics(), diagnostic => diagnostic.Message.Contains("Duplicate definition", StringComparison.Ordinal));
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

	[Fact]
	public void EditAddingUsing_RecomputesLibraryClosureWithoutProjectReopen()
	{
		const string original = "int main() { return 0; }\n";
		var x = Open(original);
		using (x.Fixture)
		{
			Assert.DoesNotContain(x.Snapshot.Documents.Values, d => IsConsoleDocument(d.FilePath));

			const string edited = "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n";
			var advanced = x.Project.Advance(Overrides(x.Snapshot, x.Document.FilePath, edited));

			Assert.Contains(advanced.InitialSnapshot.Documents.Values, d => IsConsoleDocument(d.FilePath));
			Assert.True(advanced.InitialSnapshot.TryGetDocument(x.Document.Id, out var main));
			Assert.Equal(edited, main.Text.ToString());
			Assert.Equal(original, File.ReadAllText(x.Document.FilePath));
		}
	}

	[Fact]
	public void EditRemovingUsing_RecomputesLibraryClosureCoherently()
	{
		const string original = "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n";
		var x = Open(original);
		using (x.Fixture)
		{
			Assert.Contains(x.Snapshot.Documents.Values, d => IsConsoleDocument(d.FilePath));

			const string edited = "int main() { return 0; }\n";
			var advanced = x.Project.Advance(Overrides(x.Snapshot, x.Document.FilePath, edited));

			Assert.DoesNotContain(advanced.InitialSnapshot.Documents.Values, d => IsConsoleDocument(d.FilePath));
			Assert.True(advanced.InitialSnapshot.TryGetDocument(x.Document.Id, out var main));
			Assert.Equal(edited, main.Text.ToString());
		}
	}

	[Fact]
	public void ExistingDocumentIdsRemainStableAcrossClosureChanges()
	{
		const string original = "int main() { return 0; }\n";
		var x = Open(original);
		using (x.Fixture)
		{
			var optionId = x.Snapshot.Documents.Values
				.Single(d => d.FilePath.EndsWith(Path.Combine("Core", "System", "Option.cvl"), StringComparison.OrdinalIgnoreCase))
				.Id;
			var mainId = x.Document.Id;

			const string edited = "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n";
			var advanced = x.Project.Advance(Overrides(x.Snapshot, x.Document.FilePath, edited));

			Assert.True(advanced.InitialSnapshot.TryGetDocument(mainId, out _));
			Assert.True(advanced.InitialSnapshot.TryGetDocument(optionId, out _));
		}
	}

	[Fact]
	public void NewStdDocumentIdIsStableWhenModuleLeavesAndReentersClosure()
	{
		const string original = "int main() { return 0; }\n";
		const string withUsing = "using System;\nint main() { Console.WriteLine(\"hi\"); return 0; }\n";
		const string withoutUsing = "int main() { return 0; }\n";
		var x = Open(original);
		using (x.Fixture)
		{
			var added = x.Project.Advance(Overrides(x.Snapshot, x.Document.FilePath, withUsing));
			var consoleId = added.InitialSnapshot.Documents.Values.Single(d => IsConsoleDocument(d.FilePath)).Id;

			var removed = added.Advance(Overrides(added.InitialSnapshot, x.Document.FilePath, withoutUsing));
			Assert.DoesNotContain(removed.InitialSnapshot.Documents.Values, d => IsConsoleDocument(d.FilePath));

			var reentered = removed.Advance(Overrides(removed.InitialSnapshot, x.Document.FilePath, withUsing));
			var consoleAgain = reentered.InitialSnapshot.Documents.Values.Single(d => IsConsoleDocument(d.FilePath)).Id;

			Assert.Equal(consoleId, consoleAgain);
		}
	}

	[Fact]
	public void IncompleteUsing_DoesNotCrashAndPreservesCoherentSnapshot()
	{
		const string original = "int main() { return 0; }\n";
		var x = Open(original);
		using (x.Fixture)
		{
			const string incomplete = "using \nint main() { return 0; }\n";
			var advanced = x.Project.Advance(Overrides(x.Snapshot, x.Document.FilePath, incomplete));

			Assert.Contains(advanced.InitialSnapshot.Documents.Values, d => d.FilePath.EndsWith(Path.Combine("Core", "System", "Option.cvl"), StringComparison.OrdinalIgnoreCase));
			Assert.NotEmpty(advanced.InitialSnapshot.Documents);
		}
	}

	private static bool IsConsoleDocument(string filePath) =>
		filePath.EndsWith(Path.Combine("Std", "System", "Console.cvl"), StringComparison.OrdinalIgnoreCase);

	private static IReadOnlyDictionary<string, string> Overrides(ProjectSnapshot snapshot, string path, string text)
	{
		var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var map = new Dictionary<string, string>(comparer);
		foreach (var document in snapshot.Documents.Values)
			map[document.FilePath] = document.Text.ToString();

		map[Path.GetFullPath(path)] = text;
		return map;
	}
}
