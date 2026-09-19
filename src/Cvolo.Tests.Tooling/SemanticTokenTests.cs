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
}
