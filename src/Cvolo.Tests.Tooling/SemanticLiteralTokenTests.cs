using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

public sealed class SemanticLiteralTokenTests
{
	private static List<(string Text, ToolingSymbolKind Kind)> Tokens(string body)
	{
		var source = "int main() { " + body + " return 0; }\n";
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return document.GetSemanticTokens().Select(token => (document.Text.GetText(token.Span), token.Kind)).ToList();
	}

	[Fact]
	public void DefaultKeyword_IsClassifiedAsAKeyword()
	{
		var tokens = Tokens("val x = default(int);");
		var token = Assert.Single(tokens, t => t.Text == "default");
		Assert.Equal(ToolingSymbolKind.Keyword, token.Kind);
	}

	[Fact]
	public void LanguageLiteralKeywords_AreClassifiedAsKeywords()
	{
		var tokens = Tokens("val a = null; val b = true; val c = false;");
		Assert.Equal(ToolingSymbolKind.Keyword, Assert.Single(tokens, t => t.Text == "null").Kind);
		Assert.Equal(ToolingSymbolKind.Keyword, Assert.Single(tokens, t => t.Text == "true").Kind);
		Assert.Equal(ToolingSymbolKind.Keyword, Assert.Single(tokens, t => t.Text == "false").Kind);
	}
}
