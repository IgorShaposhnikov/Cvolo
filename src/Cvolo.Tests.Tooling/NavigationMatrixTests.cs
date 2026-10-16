using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// F12/navigation regression matrix: project references stay editable, <c>using</c> aliases and
/// namespace members resolve through actual symbol binding, a generation switch drops stale
/// definitions, and a delegate-valued global navigates to its own declaration.
/// </summary>
public sealed class NavigationMatrixTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(params (string File, string Source)[] files)
	{
		var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(files[0].File)), fixture);
	}

	private static int At(string source, string needle, int delta = 0) => source.IndexOf(needle, StringComparison.Ordinal) + delta;

	[Fact]
	public void ProjectReference_DefinitionTargetsAnEditableProjectDocument()
	{
		const string main = "int main() { return Helper(); }\n";
		const string lib = "int Helper() { return 42; }\n";
		var x = Open(("Main.cvl", main), ("Lib.cvl", lib));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(main, "Helper"));
			Assert.NotNull(symbol);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(symbol!.SymbolId));
			Assert.Equal(x.Project.GetDocumentId("Lib.cvl"), definition.DocumentId);
			Assert.Equal("Helper", x.Snapshot.GetDocument(definition.DocumentId).Text.GetText(definition.SelectionSpan));
		}
	}

	[Fact]
	public void UsingAlias_SelectsTheGlobalDeclaration()
	{
		const string native = "namespace Native;\nglobal int Depth = 1;\n";
		const string main = "using N = Native;\nint main() { return N.Depth; }\n";
		var x = Open(("Main.cvl", main), ("Native.cvl", native));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(main, "Depth"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Global, symbol!.Kind);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal(x.Project.GetDocumentId("Native.cvl"), definition.DocumentId);
		}
	}

	[Fact]
	public void GenerationSwitch_DropsStaleDefinitions()
	{
		const string main = "int main() { return Helper(); }\n";
		const string lib = "int Helper() { return 1; }\n";
		var x = Open(("Main.cvl", main), ("Lib.cvl", lib));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(main, "Helper"));
			Assert.NotNull(symbol);
			Assert.Single(x.Snapshot.GetDefinitions(symbol!.SymbolId));

			var edited = x.Snapshot.WithDocument(
				x.Project.GetDocumentId("Lib.cvl"),
				SourceText.From("int Helper() { return 2; }\nint Extra() { return 3; }\n"));
			var editedDocument = edited.GetDocument(x.Project.GetDocumentId("Main.cvl"));
			var refreshed = editedDocument.GetSymbolAtPosition(At(main, "Helper"));

			Assert.NotNull(refreshed);
			var definition = Assert.Single(edited.GetDefinitions(refreshed!.SymbolId));
			Assert.Equal(x.Project.GetDocumentId("Lib.cvl"), definition.DocumentId);
			Assert.Empty(edited.GetDefinitions(symbol.SymbolId));
		}
	}

	[Fact]
	public void DelegateValuedGlobalCallSite_NavigatesToTheGlobalDeclaration()
	{
		const string native = "namespace Native;\nunsafe \"C\" delegate void GlProc();\nglobal var GlProc UniformMatrix4fv;\n";
		const string main = "using Native;\nint main() { Native.UniformMatrix4fv(); return 0; }\n";
		var x = Open(("Main.cvl", main), ("Native.cvl", native));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(main, "UniformMatrix4fv"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Global, symbol!.Kind);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal(x.Project.GetDocumentId("Native.cvl"), definition.DocumentId);
			Assert.Equal("UniformMatrix4fv", x.Snapshot.GetDocument(definition.DocumentId).Text.GetText(definition.SelectionSpan));
		}
	}
}
