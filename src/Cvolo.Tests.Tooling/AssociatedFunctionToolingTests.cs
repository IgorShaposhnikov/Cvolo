using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies that an associated function (a leading-dot extension member) behaves like any other
/// callable in editor features: it has its own symbol identity, its display signature never leaks
/// the compiler-generated synthetic receiver, and it is not confused with a same-named instance
/// method declared in the same extension block.
/// </summary>
public sealed class AssociatedFunctionToolingTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(
		params (string File, string Source)[] files)
	{
		var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(files[0].File)), fixture);
	}

	private static int At(string source, string needle, int start = 0) =>
		source.IndexOf(needle, start, StringComparison.Ordinal);

	private const string Source =
		"struct Layout { public nuint Size; }\n" +
		"public extension Layout {\n" +
		"    public nuint .AlignUp(nuint value, nuint alignment) { return value; }\n" +
		"    public nuint Scaled(nuint factor) { return Size * factor; }\n" +
		"}\n" +
		"int main() {\n" +
		"    val Layout l = Layout { Size: (nuint)8 };\n" +
		"    val nuint a = Layout.AlignUp((nuint)3, (nuint)8);\n" +
		"    val nuint b = l.Scaled((nuint)2);\n" +
		"    return 0;\n" +
		"}\n";

	[Fact]
	public void AssociatedCall_ResolvesToTheLeadingDotDeclaration()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(Source, "AlignUp((nuint)3"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.AssociatedFunction, symbol!.Kind);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));

			// The selection span covers the identifier only - never the leading dot.
			Assert.Contains(".AlignUp(nuint value", x.Document.Text.GetText(definition.Range));
			Assert.Equal("AlignUp", x.Document.Text.GetText(definition.SelectionSpan));
		}
	}

	[Fact]
	public void AssociatedCall_HoverSignatureExcludesTheSyntheticReceiver()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(Source, "AlignUp((nuint)3"));
			Assert.NotNull(symbol);
			Assert.Equal("nuint AlignUp(nuint value, nuint alignment)", symbol!.DisplayText);
			Assert.DoesNotContain("this", symbol.DisplayText, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void AssociatedCall_SignatureHelpListsDeclaredParametersOnly()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var position = At(Source, "AlignUp((nuint)3") + "AlignUp(".Length;
			var help = x.Document.GetSignatureHelp(position);

			Assert.NotNull(help);
			var signature = Assert.Single(help!.Signatures);
			Assert.Equal("nuint AlignUp(nuint value, nuint alignment)", signature.Label);
			Assert.Equal(0, signature.ActiveParameter);
			Assert.Equal(2, signature.Parameters.Count);
		}
	}

	[Fact]
	public void AssociatedAndInstanceMembers_AreDistinctSymbols()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var associated = x.Document.GetSymbolAtPosition(At(Source, "AlignUp((nuint)3"));
			var instance = x.Document.GetSymbolAtPosition(At(Source, "Scaled((nuint)2"));
			Assert.NotNull(associated);
			Assert.NotNull(instance);
			Assert.NotEqual(associated!.SymbolId, instance!.SymbolId);

			// Neither symbol claims the other's declaration.
			var associatedText = x.Document.Text.GetText(
				Assert.Single(x.Snapshot.GetDefinitions(associated.SymbolId)).SelectionSpan);
			Assert.Equal("AlignUp", associatedText);

			var instanceText = x.Document.Text.GetText(
				Assert.Single(x.Snapshot.GetDefinitions(instance.SymbolId)).SelectionSpan);
			Assert.Equal("Scaled", instanceText);
		}
	}

	[Fact]
	public void AssociatedReferences_IncludeTheTypeQualifiedCallSite()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var declaration = x.Document.GetSymbolAtPosition(At(Source, ".AlignUp") + 1);
			Assert.NotNull(declaration);

			var references = x.Snapshot.GetReferences(declaration!.SymbolId, includeDeclaration: true);
			var spans = references
				.Where(reference => reference.DocumentId == x.Document.Id)
				.Select(reference => reference.Span)
				.ToArray();

			// The declaration and the type-qualified call site are the only occurrences, and both
			// resolve to the same associated symbol.
			Assert.Equal(2, spans.Length);
			Assert.Equal("AlignUp", x.Document.Text.GetText(Assert.Single(spans, span => span.Start == At(Source, "AlignUp((nuint)3"))));
			Assert.Equal("AlignUp", x.Document.Text.GetText(Assert.Single(spans, span => span.Start == At(Source, ".AlignUp") + 1)));
		}
	}

	[Fact]
	public void AssociatedRename_UpdatesTheDeclarationAndTheTypeQualifiedCallSite()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(Source, ".AlignUp") + 1);
			Assert.NotNull(symbol);

			var result = x.Snapshot.RenameSymbol(symbol!.SymbolId, "RoundUp");
			Assert.True(result is RenameSuccess, (result as RenameFailure)?.Message);
			var success = Assert.IsType<RenameSuccess>(result);
			Assert.Equal(2, success.Edits.Count);
			Assert.All(success.Edits, edit => Assert.Equal("RoundUp", edit.NewText));

			// The instance member that shares nothing but its owner is left untouched.
			Assert.DoesNotContain(success.Edits, edit =>
				x.Document.Text.GetText(edit.Span) == "Scaled");
		}
	}

	[Fact]
	public void AssociatedDeclaration_SemanticTokenCoversOnlyTheIdentifier()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var tokens = x.Document.GetSemanticTokens()
				.Where(token => x.Document.Text.GetText(token.Span) == "AlignUp")
				.ToArray();

			Assert.Equal(2, tokens.Length);
			Assert.All(tokens, token => Assert.Equal(ToolingSymbolKind.AssociatedFunction, token.Kind));
		}
	}
}
