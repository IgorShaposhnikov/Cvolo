using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Semantic-token classification is compiler-backed and snapshot-pinned: declarations and
/// resolvable occurrences are classified, unresolved ones are omitted, and tokens are ordered.
/// </summary>
public sealed class SemanticTokenTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
	}

	private static List<SemanticTokenInfo> WithText(DocumentSnapshot document, IReadOnlyList<SemanticTokenInfo> tokens, string text)
		=> [.. tokens.Where(t => document.Text.GetText(t.Span) == text)];

	[Fact]
	public void LocalDeclarationAndUse_AreClassified()
	{
		const string s = "int main() {\n    val int count = 1;\n    return count;\n}\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "count");

			Assert.Equal(2, tokens.Count);
			Assert.All(tokens, t => Assert.Equal(ToolingSymbolKind.Local, t.Kind));
			Assert.Contains(tokens, t => t.Modifiers.HasFlag(SemanticTokenModifiers.Declaration));
			Assert.Contains(tokens, t => t.Modifiers.HasFlag(SemanticTokenModifiers.Readonly));
		}
	}

	[Fact]
	public void FunctionDeclarationAndCall_AreClassified()
	{
		const string s = "int Twice(int value) { return value + value; }\nint main() { return Twice(1); }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Twice");

			Assert.Equal(2, tokens.Count);
			Assert.All(tokens, t => Assert.Equal(ToolingSymbolKind.Function, t.Kind));
			Assert.Contains(tokens, t => t.Modifiers.HasFlag(SemanticTokenModifiers.Declaration));
		}
	}

	[Fact]
	public void Global_IsClassifiedStatic()
	{
		const string s = "global int Seed = 1;\nint main() { return Seed; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Seed");

			Assert.NotEmpty(tokens);
			Assert.Contains(tokens, t => t.Modifiers.HasFlag(SemanticTokenModifiers.Static));
			Assert.Contains(tokens, t => t.Modifiers.HasFlag(SemanticTokenModifiers.Declaration));
		}
	}

	[Fact]
	public void UnresolvedIdentifier_IsOmitted()
	{
		const string s = "int main() { return Missing; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			Assert.DoesNotContain(x.Document.GetSemanticTokens(), t => x.Document.Text.GetText(t.Span) == "Missing");
		}
	}

	[Fact]
	public void Tokens_AreAscendingAndNonOverlapping()
	{
		const string s = "struct Point { int x; }\nint main() { val Point p = Point { x: 1 }; return p.x; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var tokens = x.Document.GetSemanticTokens();

			Assert.NotEmpty(tokens);
			for (var i = 1; i < tokens.Count; i++)
				Assert.True(tokens[i - 1].Span.Start < tokens[i].Span.Start, "tokens must be in ascending source order");
		}
	}

	[Fact]
	public void SemanticKinds_AreClassifiedForDeclarations()
	{
		const string s =
			"struct PointX { int fieldY; }\n" +
			"enum ColorZ { RedW }\n" +
			"int fnQ(int paramP) { return paramP; }\n" +
			"int main() { val PointX p = PointX { fieldY: 1 }; return fnQ(p.fieldY); }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var declarations = x.Document.GetSemanticTokens().Where(t => t.Modifiers.HasFlag(SemanticTokenModifiers.Declaration)).ToList();
			ToolingSymbolKind KindOf(string text) => Assert.Single(declarations.Where(t => x.Document.Text.GetText(t.Span) == text)).Kind;

			Assert.Equal(ToolingSymbolKind.Struct, KindOf("PointX"));
			Assert.Equal(ToolingSymbolKind.Field, KindOf("fieldY"));
			Assert.Equal(ToolingSymbolKind.Enum, KindOf("ColorZ"));
			Assert.Equal(ToolingSymbolKind.EnumMember, KindOf("RedW"));
			Assert.Equal(ToolingSymbolKind.Function, KindOf("fnQ"));
			Assert.Equal(ToolingSymbolKind.Parameter, KindOf("paramP"));
			Assert.Equal(ToolingSymbolKind.Local, KindOf("p"));
		}
	}

	[Fact]
	public void ShadowedLocals_ProduceDistinctDeclarationTokens()
	{
		const string s = "int main() {\n    val int value = 1;\n    {\n        val int value = 2;\n        return value;\n    }\n}\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "value");

			Assert.Equal(3, tokens.Count);
			Assert.Equal(2, tokens.Count(t => t.Modifiers.HasFlag(SemanticTokenModifiers.Declaration)));
		}
	}

	[Fact]
	public void CrossFileSymbol_IsClassifiedInConsumer()
	{
		var fixture = TempProject.Create(
			("Main.cvl", "int main() { return Helper(); }\n"),
			("Lib.cvl", "int Helper() { return 1; }\n"));
		using (fixture)
		{
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var main = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
			var tokens = WithText(main, main.GetSemanticTokens(), "Helper");

			Assert.Single(tokens);
			Assert.Equal(ToolingSymbolKind.Function, tokens[0].Kind);
		}
	}

	[Fact]
	public void SnapshotOverlay_ChangesTokens()
	{
		const string s = "int main() { return Helper(); }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			// Unresolved in the base snapshot: no token.
			Assert.DoesNotContain(x.Document.GetSemanticTokens(), t => x.Document.Text.GetText(t.Span) == "Helper");

			// An overlay that declares Helper makes the occurrence resolvable in the derived snapshot.
			var edited = x.Snapshot.WithDocument(x.Document.Id, SourceText.From("int Helper() { return 1; }\nint main() { return Helper(); }\n"));
			var editedDocument = edited.GetDocument(x.Document.Id);
			Assert.Contains(editedDocument.GetSemanticTokens(), t => editedDocument.Text.GetText(t.Span) == "Helper");
		}
	}

	[Fact]
	public void IncompleteSource_ClassifiesResolvableTokensAndOmitsUnresolved()
	{
		const string s = "int Twice(int value) { return value + value; }\nint main() { return Twice(1) + Missing; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var tokens = x.Document.GetSemanticTokens();

			Assert.Contains(tokens, t => x.Document.Text.GetText(t.Span) == "Twice");
			Assert.DoesNotContain(tokens, t => x.Document.Text.GetText(t.Span) == "Missing");
		}
	}

	[Fact]
	public void TokenSpans_CoverExactlyTheIdentifier()
	{
		const string s = "struct Point { int value; }\nint main() { val Point p = Point { value: 1 }; return p.value; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			foreach (var token in x.Document.GetSemanticTokens())
			{
				var text = x.Document.Text.GetText(token.Span);
				Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", text);
			}
		}
	}
}
