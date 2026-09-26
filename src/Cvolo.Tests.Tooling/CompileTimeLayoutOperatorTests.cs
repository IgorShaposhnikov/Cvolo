using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class CompileTimeLayoutOperatorTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
	}

	private static int At(string source, string needle, int delta = 0) => source.IndexOf(needle, StringComparison.Ordinal) + delta;

	private static IReadOnlyList<SemanticTokenInfo> WithText(DocumentSnapshot document, IReadOnlyList<SemanticTokenInfo> tokens, string text)
		=> tokens.Where(token => document.Text.GetText(token.Span) == text).ToList();

	private const string NestedSource =
		"struct Header { int Length; }\n" +
		"struct Packet { byte Kind; Header Header; }\n" +
		"nuint Offset() { return offsetof<Packet>(Header.Length); }\n";

	[Fact]
	public void OffsetofDesignator_ResolvesToFieldDeclaration()
	{
		var x = Open(NestedSource);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(NestedSource, "Header.Length") + "Header.".Length + 2);
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Field, symbol!.Kind);
			var definition = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("Length", x.Document.Text.GetText(definition.SelectionSpan));
		}
	}

	[Fact]
	public void OffsetofDesignator_IsClassifiedAsFieldToken()
	{
		var x = Open(NestedSource);
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Length");
			Assert.Contains(tokens, token => token.Kind == ToolingSymbolKind.Field);
		}
	}

	[Fact]
	public void SizeofKeywordAndType_AreClassified()
	{
		const string s = "struct Pair { byte A; int B; }\nnuint Size() { return sizeof<Pair>(); }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var tokens = x.Document.GetSemanticTokens();
			Assert.Contains(WithText(x.Document, tokens, "sizeof"), token => token.Kind == ToolingSymbolKind.Keyword);
			Assert.Contains(WithText(x.Document, tokens, "Pair"), token => token.Kind == ToolingSymbolKind.Struct);
		}
	}

	[Fact]
	public void AlignofKeyword_IsClassified()
	{
		const string s = "struct Pair { byte A; int B; }\nnuint Align() { return alignof<Pair>(); }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			Assert.Contains(WithText(x.Document, x.Document.GetSemanticTokens(), "alignof"), token => token.Kind == ToolingSymbolKind.Keyword);
		}
	}

	[Fact]
	public void Completion_OffersLayoutOperators()
	{
		const string s = "int main()\n{\n    return 0;\n}\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var labels = x.Document.GetCompletions(At(s, "return 0;")).Candidates.Select(candidate => candidate.Label).ToHashSet(StringComparer.Ordinal);
			Assert.Contains("sizeof", labels);
			Assert.Contains("alignof", labels);
			Assert.Contains("offsetof", labels);
		}
	}
}
