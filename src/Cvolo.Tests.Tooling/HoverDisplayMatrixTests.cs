using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Hover identity must come from a coherent snapshot of the resolved binding, not from echoed source
/// spelling. These cases pin the freshness, shadowing, metadata and honest-unknown rules for the
/// inferred-type display.
/// </summary>
public sealed class HoverDisplayMatrixTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
	}

	private static int At(string source, string needle, int delta = 0) => source.IndexOf(needle, StringComparison.Ordinal) + delta;

	[Fact]
	public void InferredHover_SurvivesAWhitespaceOnlyEdit()
	{
		const string s = "struct Window { int Width; }\nint main() { var window = Window { Width = 1 }; return window.Width; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var before = x.Document.GetSymbolAtPosition(At(s, "window ="));

			var editedText = s.Replace("int main()", "\nint main()");
			var editedSnapshot = x.Snapshot.WithDocument(x.Project.GetDocumentId("Main.cvl"), SourceText.From(editedText));
			var editedDocument = editedSnapshot.GetDocument(x.Project.GetDocumentId("Main.cvl"));
			var after = editedDocument.GetSymbolAtPosition(editedText.IndexOf("window =", StringComparison.Ordinal));

			Assert.NotNull(before);
			Assert.NotNull(after);
			Assert.Equal("Window window", before!.DisplayText);
			Assert.Equal("Window window", after!.DisplayText);
		}
	}

	[Fact]
	public void ShadowedLocals_KeepDistinctHoverIdentity()
	{
		const string s =
			"struct Window { int Width; }\n" +
			"int main() { var x = Window { Width = 1 }; { var x = 5; return x; } }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var outer = x.Document.GetSymbolAtPosition(At(s, "x = Window"));
			var inner = x.Document.GetSymbolAtPosition(s.IndexOf("x = 5", StringComparison.Ordinal));
			var innerUse = x.Document.GetSymbolAtPosition(s.IndexOf("return x", StringComparison.Ordinal) + 7);

			Assert.NotNull(outer);
			Assert.NotNull(inner);
			Assert.NotNull(innerUse);
			Assert.Equal("Window x", outer!.DisplayText);
			Assert.Equal("int x", inner!.DisplayText);
			Assert.Equal("int x", innerUse!.DisplayText);
		}
	}

	[Fact]
	public void InferredHover_ComesFromBindingNotTheVarSpelling()
	{
		const string s =
			"struct Window { int Width; }\n" +
			"Window Make() { return Window { Width = 1 }; }\n" +
			"int main() { var w = Make(); return w.Width; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "w = Make"));
			Assert.NotNull(symbol);
			Assert.Equal("Window w", symbol!.DisplayText);
		}
	}

	[Fact]
	public void SameNamedParameterAndLocal_HaveDistinctKindsAndDisplays()
	{
		const string s =
			"struct Window { int Width; }\n" +
			"int Area(Window window) { return window.Width; }\n" +
			"int main() { var window = 3; return window; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var parameter = x.Document.GetSymbolAtPosition(At(s, "window)"));
			var local = x.Document.GetSymbolAtPosition(At(s, "window = 3"));

			Assert.NotNull(parameter);
			Assert.NotNull(local);
			Assert.Equal(ToolingSymbolKind.Parameter, parameter!.Kind);
			Assert.Equal(ToolingSymbolKind.Local, local!.Kind);
			Assert.Equal("Window window", parameter.DisplayText);
			Assert.Equal("int window", local.DisplayText);
		}
	}

	[Fact]
	public void DocumentedSymbolHover_RetainsDocumentationMetadata()
	{
		const string s =
			"/// Adds one to the supplied value.\n" +
			"int Increment(int value) { return value + 1; }\n" +
			"int main() { return Increment(1); }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "Increment(int value)", 3));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Function, symbol!.Kind);
			Assert.NotNull(symbol.Documentation);
		}
	}

	[Fact]
	public void BrokenInitializer_DoesNotFabricateAnInferredType()
	{
		const string unresolved = "int main() { var q = Missing; return 0; }\n";
		var a = Open(unresolved);
		using (a.Fixture)
		{
			var symbol = a.Document.GetSymbolAtPosition(At(unresolved, "q = Missing"));
			Assert.NotNull(symbol);
			Assert.Equal("var q", symbol!.DisplayText);
		}

		const string declared =
			"struct Window { void* Ptr; int Width; }\n" +
			"int main() { Window windo = Window\n{\n\n}; return 0; }\n";
		var b = Open(declared);
		using (b.Fixture)
		{
			var symbol = b.Document.GetSymbolAtPosition(At(declared, "windo = Window"));
			Assert.NotNull(symbol);
			Assert.Equal("Window windo", symbol!.DisplayText);
			Assert.DoesNotContain("int windo", symbol.DisplayText, StringComparison.Ordinal);
		}
	}
}
